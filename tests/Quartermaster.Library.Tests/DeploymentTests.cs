using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Storage;
using Xunit;

namespace Quartermaster.Library.Tests;

public class DeploymentTests
{
    [Fact]
    public async Task DeployRedeployAndPurgePreserveOriginalsAndReplaceAllPatches()
    {
        using var f = new Fixture();
        var a = await f.Library.ImportAsync(f.Source("a", 1));
        var b = await f.Library.ImportAsync(f.Source("b", 2));
        var other = await f.Library.ImportAsync(f.Source("other", 3, "0123456789abcdef"));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Default"), a), b), other);
        await f.Library.SaveProfileAsync(profile, true);
        var foreign = Path.Combine(f.Game, Fixture.Archive + ".patch_0.stream");
        var deployment = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents)));
        var state = await f.Library.LoadAsync();
        var plan = await deployment.PreviewAsync(state, profile.Id, f.Game);
        Assert.Equal(new[] { 0, 1, 0 }, plan.Patches.Select(p => p.Slot));
        var ledger = await deployment.DeployAsync(state, profile.Id, f.Game);
        Assert.Equal(6, ledger.Files.Count);
        Assert.Equal(Fixture.Patch(2), File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_1")));
        var health = await deployment.InspectAsync(f.Game);
        Assert.All(health.Files, file => Assert.Equal(ManagedFileStatus.Present, file.Status));
        Assert.Equal(ModDeploymentStatus.Deployed, DeploymentTracking.ForMod(a.Id, plan, ledger, health.Files));
        profile = ProfileEditor.SetEnabled(profile, b.Id, false);
        await f.Library.SaveProfileAsync(profile);
        ledger = await deployment.DeployAsync(await f.Library.LoadAsync(), profile.Id, f.Game);
        Assert.Equal(4, ledger.Files.Count); Assert.False(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_1")));
        File.WriteAllBytes(foreign, [0xfe]);
        await deployment.PurgeAsync(f.Game);
        Assert.False(File.Exists(foreign));
        Assert.Empty((await deployment.InspectAsync(f.Game)).Ledger.Files);
        Assert.Equal(Fixture.Patch(1), File.ReadAllBytes(f.Contents.GetFilePath(a.Id, a.PatchSets[0].Files[0])));
    }

    [Fact]
    public async Task ModifiedFilesRequirePurgeAndCanBeRemoved()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        var path = Path.Combine(f.Game, ledger.Files[0].Name); File.WriteAllBytes(path, [9]);
        Assert.Contains((await service.InspectAsync(f.Game)).Files, file => file.Status == ManagedFileStatus.Modified);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        await service.PurgeAsync(f.Game);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(f.App, "deployment.lock")));
    }

    [Fact]
    public async Task MissingFilesRequirePurgeThenRedeploy()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        File.Delete(Path.Combine(f.Game, ledger.Files[0].Name));
        Assert.Contains((await service.InspectAsync(f.Game)).Files, file => file.Status == ManagedFileStatus.Missing);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        await service.PurgeAsync(f.Game);
        await service.DeployAsync(state, profile.Id, f.Game);
        Assert.All((await service.InspectAsync(f.Game)).Files, file => Assert.Equal(ManagedFileStatus.Present, file.Status));
    }

    [Fact]
    public async Task FailedPublicationLeavesIncompleteManifestAndNoBackups()
    {
        using var f = new Fixture(); var old = await f.Library.ImportAsync(f.Source("old", 1));
        var source = f.Source("new", 2, slot: 0);
        File.WriteAllBytes(Path.Combine(source, Fixture.Archive + ".patch_1"), Fixture.Patch(3));
        var newer = await f.Library.ImportAsync(source);
        var oldProfile = ProfileEditor.Add(ProfileEditor.Create("Old"), old); var newProfile = ProfileEditor.Add(ProfileEditor.Create("New"), newer);
        await f.Library.SaveProfileAsync(oldProfile); await f.Library.SaveProfileAsync(newProfile);
        var state = await f.Library.LoadAsync(); var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents)));
        var original = await service.DeployAsync(state, oldProfile.Id, f.Game);
        // A directory at the second new main destination forces failure after files were changed.
        Directory.CreateDirectory(Path.Combine(f.Game, Fixture.Archive + ".patch_1"));
        await Assert.ThrowsAnyAsync<IOException>(() => service.DeployAsync(state, newProfile.Id, f.Game));
        var incomplete = await service.InspectAsync(f.Game);
        Assert.Equal(DeploymentStatus.Deploying, incomplete.Ledger.Status);
        Assert.NotEqual(original.Signature, incomplete.Ledger.Signature);
        Assert.True(incomplete.NeedsPurge);
        Assert.False(Directory.Exists(Path.Combine(f.App, "deployments")));
        Directory.Delete(Path.Combine(f.Game, Fixture.Archive + ".patch_1"));
        await service.PurgeAsync(f.Game);
        await service.DeployAsync(state, newProfile.Id, f.Game);
        Assert.False((await service.InspectAsync(f.Game)).NeedsPurge);
        Assert.Empty(Directory.GetFiles(f.Game, "*.tmp"));
    }

    [Fact]
    public async Task TamperedLibraryFilesAndCancelledOperationsDoNotChangeDeployment()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        File.WriteAllBytes(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files[0]), [0]);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PurgeAsync(f.Game, cancellation.Token));
        Assert.Equal(ledger.Signature, (await service.InspectAsync(f.Game)).Ledger.Signature);
        Assert.Equal(Fixture.Patch(), File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
    }

    [Fact]
    public async Task OptionalRepairPersistsSeparateCopiesAndRecordsSourceHashes()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents), new FakeRepair()));
        var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game, new(Repatch: true));
        var main = ledger.Files.Single(file => file.Kind == PatchFileKind.Main);
        Assert.NotEqual(main.SourceSha256, main.Sha256);
        var catalog = global::System.Text.Json.JsonSerializer.Deserialize<RepairCatalog>(
            await File.ReadAllTextAsync(Path.Combine(f.App, "patches.json")), new global::System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new global::System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        var repair = Assert.Single(catalog.Repairs);
        Assert.Equal(mod.Id, repair.ModId);
        Assert.Equal(0xaa, File.ReadAllBytes(Path.Combine(f.App, repair.Directory, main.Name))[^1]);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.App, repair.Directory)).Length);

        Assert.Equal(0xaa, File.ReadAllBytes(Path.Combine(f.Game, main.Name))[^1]);
        Assert.Equal(Fixture.Patch(), File.ReadAllBytes(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files[0])));
        var health = await service.InspectAsync(f.Game);
        Assert.Equal(ModDeploymentStatus.Deployed, DeploymentTracking.ForMod(mod.Id, await service.PreviewAsync(state, profile.Id, f.Game), ledger, health.Files));
        service = new(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents), new FakeRepair(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DeployAsync(state, profile.Id, f.Game, new(Repatch: true)));
        Assert.Equal(ledger.Signature, (await service.InspectAsync(f.Game)).Ledger.Signature);
    }

    private sealed class FakeRepair(int removed = 0) : IPatchRepairer
    {
        public RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default)
        { var bytes = patch.ToArray(); bytes[^1] = 0xaa; return new(bytes, removed); }
    }
}
