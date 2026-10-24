using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Quartermaster.Core.Deployment;
using Quartermaster.FileMerger;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using ConfigFixture = Quartermaster.FileMerger.Tests.Fixture;
using Xunit;

namespace Quartermaster.Library.Tests;

public sealed class FileModIntegrationTests
{
    private static string Package(Fixture f, ConfigFixture config, string name, int offset, uint value)
    {
        var folder = Path.Combine(f.Root, name); Directory.CreateDirectory(Path.Combine(folder, "Install")); Directory.CreateDirectory(Path.Combine(folder, "UninstallHelper"));
        File.WriteAllBytes(Path.Combine(folder, "Install", "b5b0c0ffee5b5b11.patch_0"), ConfigFixture.Patch(config.Payload(config.Mod(name, offset, value))));
        var helper = ConfigFixture.Patch(Encoding.UTF8.GetBytes("-- HD2 file-mod installer 1 (shared)"));
        File.WriteAllBytes(Path.Combine(folder, "Install", Fixture.Archive + ".patch_0"), helper);
        File.WriteAllBytes(Path.Combine(folder, "UninstallHelper", Fixture.Archive + ".patch_0"), helper);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """{"Options":[{"Name":"Install","Include":["Install"]},{"Name":"Uninstall helper","Include":["UninstallHelper"]}]}""");
        return folder;
    }
    private static string GameData(ConfigFixture config)
    {
        var data = Path.Combine(config.Game, "data"); File.WriteAllBytes(Path.Combine(data, Fixture.Archive), []); return data;
    }

    [Theory]
    [InlineData(PriorityDirection.LastWins, 300u)]
    [InlineData(PriorityDirection.FirstWins, 200u)]
    public async Task RoutesPackedFileModsAndRespectsBothProfilePriorities(PriorityDirection priority, uint expected)
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var a = await f.Library.ImportAsync(Package(f, config, "Vaulting", 0, 200));
        var b = await f.Library.ImportAsync(Package(f, config, "Sliding", 0, 300));
        Assert.True(a.IsFileBased); Assert.False(a.UsesLua);
        Assert.Equal(2, a.PatchSets.Count(set => set.ContentKind == ModContentKind.FileModInstaller));
        Assert.True(a.IsInstallerOption(a.Options.Single(option => option.Name == "Uninstall helper")));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("File mods") with { Priority = priority }, a), b);
        await f.Library.SaveProfileAsync(profile, true);
        var state = await f.Library.LoadAsync(); Assert.Empty(ProfilePatches.Resolve(state, profile).Patches);
        var service = new ProfileFileMods(f.Contents); var data = GameData(config);
        var plan = await service.PrepareAsync(state, profile, data, CancellationToken.None);
        Assert.Single(plan.Plan.Conflicts); Assert.Contains("Vaulting", plan.Warnings[0]); Assert.Contains("Sliding", plan.Warnings[0]);
        await service.BeginAsync(plan, data, CancellationToken.None); await service.ApplyAsync(plan, data, CancellationToken.None);
        var output = GameConfigCodec.Decrypt(await File.ReadAllBytesAsync(Path.Combine(config.Game, ConfigFixture.Target)));
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28)));
        var health = await service.InspectAsync(data, CancellationToken.None);
        Assert.True(health.Complete); Assert.Equal(plan.Signature, health.Signature); Assert.Equal(2, health.AppliedModIds.Count);
        await service.RestoreAsync(data, CancellationToken.None);
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(Path.Combine(config.Game, ConfigFixture.Target)));
    }

    [Fact]
    public async Task RawFolderVariantsAndStreamFramesBecomeSelectableAndRoundTripInProfiles()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var source = Path.Combine(f.Root, "raw");
        foreach (var (name, value) in new[] { ("Water", 200u), ("WaterVault", 300u) })
        {
            var directory = Path.Combine(source, name); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "generated_entities.dl_bin"), config.Mod(name, 0, value).Content.ToArray());
        }
        var raw = await f.Library.ImportAsync(f.Zip(source));
        Assert.True(raw.IsFileBased); Assert.Equal(2, Assert.Single(raw.Options).Choices.Count);
        var streamPath = Path.Combine(f.Root, "Variants.stream");
        await File.WriteAllBytesAsync(streamPath, config.Payload(config.Mod("A", 0, 400)).Concat(config.Payload(config.Mod("B", 0, 500))).ToArray());
        var stream = await f.Library.ImportAsync(streamPath);
        Assert.Equal(2, Assert.Single(stream.Options).Choices.Count);
        var profile = ProfileEditor.SetOptions(ProfileEditor.Add(ProfileEditor.Create("Variants"), stream), stream, [new(stream.Options[0].Id, true, 1)]);
        await f.Library.SaveProfileAsync(profile, true);
        var service = new ProfileFileMods(f.Contents); var data = GameData(config);
        var plan = await service.PrepareAsync(await f.Library.LoadAsync(), profile, data, CancellationToken.None);
        Assert.Empty(plan.Plan.Conflicts); Assert.Equal(500u, BinaryPrimitives.ReadUInt32LittleEndian(GameConfigCodec.Decrypt(plan.Plan.Files[0].Content.Span).AsSpan(28)));
        var path = Path.Combine(f.Root, "profile.zip"); var archives = new ProfileArchives(f.Store, f.Contents);
        await archives.ExportAsync(profile.Id, path);
        var imported = await archives.ImportAsync(path);
        Assert.Equal(1, Assert.Single(imported.Entries[0].Options).ChoiceIndex);
    }

    [Fact]
    public async Task RawFoldersEditingDifferentFilesAreIndependentRatherThanExclusiveVariants()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var source = Path.Combine(f.Root, "Multi-file mod");
        foreach (var (folder, filename) in new[] { ("Movement", "generated_entities.dl_bin"), ("Stratagems", "generated_stratagem_settings.dl_bin") })
        {
            Directory.CreateDirectory(Path.Combine(source, folder));
            await File.WriteAllBytesAsync(Path.Combine(source, folder, filename), config.Mod(folder, 0, 200).Content.ToArray());
            await File.WriteAllBytesAsync(Path.Combine(config.Game, "data/game", filename), config.Stock);
        }
        var mod = await f.Library.ImportAsync(source); Assert.Empty(mod.Options);
        var profile = ProfileEditor.Add(ProfileEditor.Create("Multi-file"), mod); await f.Library.SaveProfileAsync(profile, true);
        var service = new ProfileFileMods(f.Contents);
        var prepared = await service.PrepareAsync(await f.Library.LoadAsync(), profile, GameData(config), CancellationToken.None);
        Assert.Equal(2, prepared.Plan.Files.Count);
    }

    [Fact]
    public async Task RepatchedExportsPreserveFileModPayloadsAndVerifyTheirOriginalHashes()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var mod = await f.Library.ImportAsync(Package(f, config, "Water", 0, 200));
        var output = Path.Combine(f.Root, "exported.zip");
        await f.Contents.ExportRepatchedAsync(mod, output, new NoArchiveRepair());
        Assert.Equal(mod.Id, (await f.Library.ImportAsync(output)).Id);
        var source = mod.PatchSets.Single(set => set.ContentKind == ModContentKind.FileMod).Files[0];
        await File.WriteAllBytesAsync(f.Contents.GetFilePath(mod.Id, source), [0]);
        var error = await Assert.ThrowsAsync<IOException>(() => f.Contents.ExportRepatchedAsync(mod, output, new NoArchiveRepair()));
        Assert.Contains("Water", error.Message);
    }

    [Fact]
    public async Task LegacyImportsAreDetectedAndHybridModsKeepOrdinaryPatches()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var source = Package(f, config, "Hybrid", 0, 200);
        var cosmetic = f.Source("cosmetic", 9);
        File.Move(Path.Combine(cosmetic, Fixture.Archive + ".patch_7"), Path.Combine(source, "cosmetic.patch_7"));
        // Use a valid archive basename for an unrelated normal patch.
        File.Move(Path.Combine(source, "cosmetic.patch_7"), Path.Combine(source, "1111111111111111.patch_7"));
        var mod = await f.Library.ImportAsync(source);
        var profile = ProfileEditor.Add(ProfileEditor.Create("Hybrid"), mod);
        await f.Store.SaveAsync(new(LibraryState.CurrentSchemaVersion,
            [mod with { FileDetectionVersion = 0, PatchSets = mod.PatchSets.Select(set => set with { ContentKind = ModContentKind.ArchivePatch }).ToArray() }], [profile], profile.Id));
        var state = await f.Library.LoadAsync(); Assert.True(state.Mods.Single().IsFileBased);
        Assert.Equal("1111111111111111", Assert.Single(ProfilePatches.Resolve(state, profile).Patches).Archive);
    }

    [Fact]
    public async Task VersionMismatchFailsBeforeAnyDeploymentAndChangedFilesAreUnhealthy()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        var mod = await f.Library.ImportAsync(Package(f, config, "Water", 0, 200));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Water"), mod); await f.Library.SaveProfileAsync(profile, true);
        var service = new ProfileFileMods(f.Contents); var data = GameData(config); var target = Path.Combine(config.Game, ConfigFixture.Target);
        var changed = GameConfigCodec.Encrypt(ConfigFixture.Config(firstValue: 900)); await File.WriteAllBytesAsync(target, changed);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.PrepareAsync(awaitState(), profile, data, CancellationToken.None));
        Assert.Contains("Water", error.Message); Assert.Equal(changed, await File.ReadAllBytesAsync(target));
        await File.WriteAllBytesAsync(target, config.Stock);
        var plan = await service.PrepareAsync(await f.Library.LoadAsync(), profile, data, CancellationToken.None);
        await service.BeginAsync(plan, data, CancellationToken.None);
        Assert.False((await service.InspectAsync(data, CancellationToken.None)).Complete);
        await service.ApplyAsync(plan, data, CancellationToken.None);
        await File.WriteAllBytesAsync(target, changed);
        Assert.False((await service.InspectAsync(data, CancellationToken.None)).Complete);
        LibraryState awaitState() => new(LibraryState.CurrentSchemaVersion, [mod], [profile], profile.Id);
    }

    private sealed class NoArchiveRepair : IPatchRepairer
    {
        public RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("File mods and their installer helpers must not be repatched.");
    }
}
