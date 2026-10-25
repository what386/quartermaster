using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Quartermaster.FileMerger.Tests;

public sealed class MergerTests
{
    [Theory]
    [InlineData("", 0xd9f18670253a13e6UL)]
    [InlineData("61", 0x83f39880a1385fb7UL)]
    [InlineData("4c444c44", 0x2d7c3a1d504018b2UL)]
    [InlineData("0001020304050607", 0x053d78119f7fa454UL)]
    [InlineData("000102030405060708090a0b0c0d0e0f10", 0x981657a03520eb7bUL)]
    public void HashMatchesIndependentReferenceVectors(string hex, ulong expected) => Assert.Equal(expected, ConfigHash.Compute(Convert.FromHexString(hex)));

    [Fact]
    public void CodecAuthenticatesAndRejectsCorruption()
    {
        using var f = new Fixture();
        Assert.Equal(f.Plain, GameConfigCodec.Decrypt(f.Stock));
        var corrupt = f.Stock.ToArray(); corrupt[50] ^= 1;
        Assert.Throws<InvalidDataException>(() => GameConfigCodec.Decrypt(corrupt));
        Assert.Throws<InvalidDataException>(() => GameConfigCodec.Decrypt([1, 2]));
    }

    [Fact]
    public void MergesIndependentChangesAndPreservesGameHashAndSourceBytes()
    {
        using var f = new Fixture();
        var a = f.Mod("Water", 0, 200); var b = f.Mod("Vaulting", 4, 300);
        var source = a.Content.ToArray();
        var result = ConfigMerger.Merge(Fixture.Target, f.Stock, [a, b]);
        var output = GameConfigCodec.Decrypt(result.Content);
        Assert.Equal(200u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28)));
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(32)));
        Assert.Equal(f.Plain.AsSpan(36, 56).ToArray(), output.AsSpan(36, 56).ToArray());
        Assert.Equal(ConfigHash.Compute(f.Plain), ConfigHash.Compute(output));
        Assert.Equal(source, a.Content.ToArray());
        Assert.Empty(result.Conflicts); Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ConflictsUseWholeWordsAndLastPriorityWinsWithoutDroppingOtherChanges()
    {
        using var f = new Fixture();
        var a = f.Mod("Vaulting", 0, 0x00010064); var b = f.Mod("Sliding", 0, 0x00000164);
        var extra = f.Mod("Water", 4, 300);
        var result = ConfigMerger.Merge(Fixture.Target, f.Stock, [a, extra, b]);
        var output = GameConfigCodec.Decrypt(result.Content);
        Assert.Equal(0x00000164u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(28)));
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(32)));
        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal("Vaulting", conflict.LosingMod); Assert.Equal("Sliding", conflict.WinningMod); Assert.Equal(0, conflict.Offset);
        Assert.Equal(0x10203040u, conflict.BlockType);
        var reversed = ConfigMerger.Merge(Fixture.Target, f.Stock, [b, a]);
        Assert.Equal(0x00010064u, BinaryPrimitives.ReadUInt32LittleEndian(GameConfigCodec.Decrypt(reversed.Content).AsSpan(28)));
        Assert.Empty(ConfigMerger.Merge(Fixture.Target, f.Stock, [a, a with { Name = "Same edit" }]).Conflicts);
    }

    [Theory]
    [InlineData(400u, 2)]
    [InlineData(200u, 1)]
    public void ConflictReportsUseFinalWinnerAndExcludeEditsMatchingItsValue(uint finalValue, int count)
    {
        using var f = new Fixture();
        var first = f.Mod("First", 0, 200) with { Source = "first" };
        var middle = f.Mod("Middle", 0, 300) with { Source = "middle" };
        var last = f.Mod("Last", 0, finalValue) with { Source = "last" };
        var result = ConfigMerger.Merge(Fixture.Target, f.Stock, [first, middle, last]);
        Assert.Equal(count, result.Conflicts.Count);
        Assert.All(result.Conflicts, conflict =>
        {
            Assert.Equal("Last", conflict.WinningMod);
            Assert.Equal("last", conflict.WinningSource);
            Assert.NotNull(conflict.LosingSource);
        });
        Assert.Equal(finalValue, BinaryPrimitives.ReadUInt32LittleEndian(GameConfigCodec.Decrypt(result.Content).AsSpan(28)));
        Assert.Contains(result.Conflicts, conflict => conflict.LosingSource == "middle");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LayoutChangingModsAreExclusiveAndReportEverySkippedMod(bool structuralWins)
    {
        using var f = new Fixture();
        var structural = new FileMod("Jump pack", Fixture.Target,
            GameConfigCodec.EncryptPreservingHash(Fixture.Config(68), ConfigHash.Compute(f.Plain)), "jump.patch_0", [FileModPaths.Fingerprint(f.Stock)]);
        var normal = f.Mod("Water", 0, 200);
        var result = ConfigMerger.Merge(Fixture.Target, f.Stock, structuralWins ? [normal, structural] : [structural, normal]);
        var winner = structuralWins ? structural : normal;
        Assert.Equal(winner.Content.ToArray(), result.Content);
        Assert.Equal(structuralWins ? "Water" : "Jump pack", Assert.Single(result.Skipped).Name);
        Assert.Contains(winner.Name, result.Skipped[0].Reason);
        Assert.Equal(winner.Name, result.Skipped[0].WinningMod);
        Assert.Equal(winner.Source, result.Skipped[0].WinningSource);
    }

    [Fact]
    public void OldVersionsAndMismatchedTargetsFailWithModIdentity()
    {
        using var f = new Fixture(); var mod = f.Mod("Water", 0, 200);
        var changedStock = GameConfigCodec.Encrypt(Fixture.Config(firstValue: 900));
        var failure = Assert.Throws<InvalidDataException>(() => ConfigMerger.Merge(Fixture.Target, changedStock, [mod]));
        Assert.Contains("Water", failure.Message); Assert.Contains("Needs a new version", failure.Message);
        Assert.Throws<InvalidDataException>(() => ConfigMerger.Merge(Fixture.Target, changedStock, [mod with { StockFingerprints = [] }]));
        Assert.Throws<InvalidDataException>(() => ConfigMerger.Merge(Fixture.Target, f.Stock, [mod with { Target = "data/game/generated_other.dl_bin" }]));
        Assert.Throws<InvalidDataException>(() => FileModPaths.ValidateTarget("data/game/../../game.dll"));
        Assert.Throws<OperationCanceledException>(() => ConfigMerger.Merge(Fixture.Target, f.Stock, [mod], new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectsPackedVariantsAndHelperWithoutExecutingLua(bool zip)
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "source");
        foreach (var variant in new[] { "Water", "WaterVault" })
        {
            var directory = Path.Combine(folder, "Install", variant); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "b5b0c0ffee5b5b11.patch_0"), Fixture.Patch(f.Payload(f.Mod(variant, 0, 200))));
        }
        var helper = Path.Combine(folder, "UninstallHelper"); Directory.CreateDirectory(helper);
        await File.WriteAllBytesAsync(Path.Combine(helper, "9ba626afa44a3aa3.patch_0"), Fixture.Patch(Encoding.UTF8.GetBytes("-- HD2 file-mod installer 1 (helper)\nerror('must not run')")));
        await File.WriteAllTextAsync(Path.Combine(folder, "plain.lua"), "error('must not run')");
        var source = folder;
        if (zip) { source = Path.Combine(f.Root, "mod.zip"); ZipFile.CreateFromDirectory(folder, source); }
        var result = await new FileModDetector().DetectAsync(source);
        Assert.Equal(new[] { "Water", "WaterVault" }, result.Mods.Select(mod => mod.Name));
        Assert.All(result.Mods, mod => Assert.Equal(Fixture.Target, mod.Target));
        Assert.Contains("UninstallHelper", Assert.Single(result.InstallerFiles));
        Assert.Contains("WaterVault", result.Mods[1].Source);
    }

    [Fact]
    public async Task DetectsPlainConfigsAndMultipleStreamPayloads()
    {
        using var f = new Fixture(); var mod = f.Mod("Water", 0, 200);
        var path = Path.Combine(f.Root, "generated_entities.dl_bin");
        await File.WriteAllBytesAsync(path, mod.Content.ToArray());
        var result = await new FileModDetector().DetectAsync(path);
        Assert.Equal(Fixture.Target, Assert.Single(result.Mods).Target);
        var stream = Path.Combine(f.Root, "bundle.stream");
        await File.WriteAllBytesAsync(stream, new byte[] { 0, 1, 2 }.Concat(f.Payload(mod)).Concat(f.Payload(mod with { Name = "Other" })).ToArray());
        Assert.Equal(new[] { "Water", "Other" }, (await new FileModDetector().DetectAsync(stream)).Mods.Select(mod => mod.Name));
    }

    [Fact]
    public async Task RejectsDamagedPayloadsUnsafeArchivesAndResourceRanges()
    {
        using var f = new Fixture(); var mod = f.Mod("Water", 0, 200);
        var file = Path.Combine(f.Root, "payload.stream"); var payload = f.Payload(mod); payload[^5] ^= 1;
        await File.WriteAllBytesAsync(file, payload);
        Assert.Contains("fingerprint", (await Assert.ThrowsAsync<InvalidDataException>(() => new FileModDetector().DetectAsync(file))).Message);
        await File.WriteAllBytesAsync(file, "HD2FM1\xff\xff\xff\xff"u8.ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileModDetector().DetectAsync(file));
        var patch = Fixture.Patch(f.Payload(mod)); BinaryPrimitives.WriteUInt64LittleEndian(patch.AsSpan(120), ulong.MaxValue);
        var patchPath = Path.Combine(f.Root, "x.patch_0"); await File.WriteAllBytesAsync(patchPath, patch);
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileModDetector().DetectAsync(patchPath));
        var zip = Path.Combine(f.Root, "bad.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../payload.stream").Open()); writer.Write("data"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileModDetector().DetectAsync(zip));
        await Assert.ThrowsAsync<InvalidDataException>(() => new FileModDetector(new(MaxFileBytes: 8)).DetectAsync(file));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FileModDetector().DetectAsync(file, new CancellationToken(true)));
    }
}
