using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;
using Xunit;

namespace Quartermaster.Repatcher.Tests;

public class RepatcherTests
{
    public static IEnumerable<object[]> ReferenceCases()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "repair-reference.json")));
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
            yield return [item.GetProperty("name").GetString()!, item.Clone()];
    }

    [Theory]
    [MemberData(nameof(ReferenceCases))]
    public void MatchesPythonReference(string name, System.Text.Json.JsonElement reference)
    {
        Assert.NotEmpty(name);
        var units = reference.GetProperty("units").EnumerateObject()
            .Select(u => (ulong.Parse(u.Name), Convert.FromHexString(u.Value.GetString()!))).ToArray();
        var result = new Repatcher(new UnitSource(units)).RepairPatch(
            Convert.FromHexString(reference.GetProperty("patch").GetString()!));
        Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Equal(Convert.FromHexString(reference.GetProperty("expected").GetString()!), result.Data);
    }

    private static byte[] Payload(byte[] patch, ResourceRecord record) => patch.AsSpan((int)record.DataOffset, (int)record.DataSize).ToArray();

    [Theory]
    [InlineData(8, 24)]
    [InlineData(24, 8)]
    [InlineData(8, 8)]
    public void UpdatesVersionLodSizesOffsetsAndPreservesOtherResources(int oldSize, int newSize)
    {
        var mod = Fixtures.Unit(oldSize, 1);
        var game = Fixtures.Unit(newSize, 0xA4CD40, 0xee);
        var tail = new byte[] { 1, 2, 3, 4, 5 };
        var source = Fixtures.Patch((1, PatchTable.UnitTypeId, mod), (2, Fixtures.OtherType, tail));
        var snapshot = source.ToArray();
        var result = new Repatcher(new UnitSource((1, game))).RepairPatch(source);
        Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Equal(1, result.RepairedUnits);
        Assert.Equal(snapshot, source);
        Assert.Equal(source.Length + newSize - oldSize, result.Data!.Length);
        var table = PatchTable.Read(result.Data);
        var unit = Payload(result.Data, table.Resources[0]);
        Assert.Equal((uint)0xA4CD40, Fixtures.Read32(unit, 0x2c));
        Assert.Equal((uint)(0x80 + newSize), Fixtures.Read32(unit, 0x34));
        Assert.Equal((uint)(0x84 + newSize), Fixtures.Read32(unit, 0x38));
        Assert.Equal(game.AsSpan(0x80, newSize).ToArray(), unit.AsSpan(0x80, newSize).ToArray());
        Assert.Equal(mod[^16..], unit[^16..]);
        var layout = (int)Fixtures.Read32(unit, 0x5c);
        Assert.Equal(21u, Fixtures.Read32(unit, layout + 20));
        Assert.Equal(16u, Fixtures.Read32(unit, layout + 40));
        Assert.Equal(tail, Payload(result.Data, table.Resources[1]));
        Assert.Equal(0xfedcba9876543210, table.Resources[0].Unknown1);
        Assert.Equal(123UL, table.Resources[0].StreamOffset);
        Assert.Equal(456UL, table.Resources[0].GpuOffset);
        Assert.Equal(91u, table.Resources[0].Unknown3);
        Assert.Equal(0x99, result.Data[20]);
        Assert.Equal(result.Data, new Repatcher(new UnitSource((1, game))).RepairPatch(result.Data).Data);
    }

    [Fact]
    public void ModernLayoutsAreNotUpgradedAgain()
    {
        var unit = Fixtures.Unit(8);
        var patch = Fixtures.Patch((1, PatchTable.UnitTypeId, unit));
        var result = new Repatcher(new UnitSource((1, unit))).RepairPatch(patch);
        Assert.Equal(patch, result.Data);
    }

    [Fact]
    public void RemovesMissingUnitsAndShiftsTocAndPayloads()
    {
        var unit = Fixtures.Unit(8);
        var patch = Fixtures.Patch((99, PatchTable.UnitTypeId, unit), (1, PatchTable.UnitTypeId, unit), (2, Fixtures.OtherType, new byte[] { 9 }));
        var before = PatchTable.Read(patch);
        var result = new Repatcher(new UnitSource((1, unit))).RepairPatch(patch);
        Assert.Equal(1, result.RemovedUnits);
        var table = PatchTable.Read(result.Data!);
        Assert.Equal(2, table.Resources.Count);
        Assert.Equal(1UL, table.Types[0].Count);
        Assert.Equal(before.Resources[1].DataOffset - 80, table.Resources[0].DataOffset);
        Assert.Equal(unit, Payload(result.Data!, table.Resources[0]));
        Assert.Equal(new byte[] { 9 }, Payload(result.Data!, table.Resources[1]));
    }

    [Fact]
    public void CanRemoveAllUnits()
    {
        var patch = Fixtures.Patch((99, PatchTable.UnitTypeId, Fixtures.Unit(8)));
        var result = new Repatcher(new UnitSource()).RepairPatch(patch);
        Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Empty(PatchTable.Read(result.Data!).Resources);
        Assert.Equal(1, result.RemovedUnits);
    }

    [Fact]
    public void HandlesUnsortedItemTableAndMultipleResizes()
    {
        var patch = Fixtures.Patch((1, PatchTable.UnitTypeId, Fixtures.Unit(8)), (2, PatchTable.UnitTypeId, Fixtures.Unit(24)));
        // Reverse table rows without changing their physical payload order.
        var first = patch.AsSpan(104, 80).ToArray();
        patch.AsSpan(184, 80).CopyTo(patch.AsSpan(104)); first.CopyTo(patch, 184);
        var game1 = Fixtures.Unit(24, fill: 0xee); var game2 = Fixtures.Unit(8, fill: 0xff);
        var result = new Repatcher(new UnitSource((1, game1), (2, game2))).RepairPatch(patch);
        Assert.Equal(RepairStatus.Updated, result.Status);
        var table = PatchTable.Read(result.Data!);
        Assert.Equal(game2, Payload(result.Data!, table.Resources[0]));
        Assert.Equal(game1, Payload(result.Data!, table.Resources[1]));
    }

    [Theory]
    [InlineData("magic")]
    [InlineData("truncated")]
    [InlineData("count")]
    [InlineData("range")]
    [InlineData("overlap")]
    [InlineData("lod")]
    [InlineData("layout")]
    public void RejectsMalformedPatchesWithoutOutput(string corruption)
    {
        var unit = Fixtures.Unit(8, 1);
        var patch = Fixtures.Patch((1, PatchTable.UnitTypeId, unit), (2, PatchTable.UnitTypeId, unit));
        var table = PatchTable.Read(patch);
        var offset = (int)table.Resources[0].DataOffset;
        switch (corruption)
        {
            case "magic": patch[0] = 0; break;
            case "truncated": Array.Resize(ref patch, 30); break;
            case "count": Fixtures.U64(patch, 88, 1); break;
            case "range": Fixtures.U64(patch, 120, ulong.MaxValue); break;
            case "overlap": Fixtures.U64(patch, 200, (ulong)offset); break;
            case "lod": Fixtures.U32(patch, offset + 0x34, 0); break;
            case "layout": Fixtures.U32(patch, offset + 0x5c, uint.MaxValue); break;
        }
        var result = new Repatcher(new UnitSource((1, unit), (2, unit))).RepairPatch(patch);
        Assert.Equal(RepairStatus.Corrupted, result.Status);
        Assert.Null(result.Data);
        Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void NoUnitPatchIsUnchanged()
    {
        var patch = Fixtures.Patch((1, Fixtures.OtherType, new byte[] { 1, 2 }));
        var result = new Repatcher(new UnitSource()).RepairPatch(patch);
        Assert.Equal(RepairStatus.NoUnits, result.Status);
        Assert.Equal(patch, result.Data);
    }

    [Fact]
    public async Task StagingPreservesOriginalAndCompanionsAndRefusesOverwrite()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, Fixtures.ArchiveName + ".patch_0");
        var destination = Path.Combine(temp.Path, "stage", Path.GetFileName(source));
        var patch = Fixtures.Patch((1, PatchTable.UnitTypeId, Fixtures.Unit(24)));
        File.WriteAllBytes(source, patch);
        File.WriteAllBytes(source + ".stream", [1, 2, 3]);
        File.WriteAllBytes(source + ".gpu_resources", [4, 5, 6]);
        var engine = new Repatcher(new UnitSource((1, Fixtures.Unit(8))));
        var result = await engine.RepairFileAsync(source, destination);
        Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Equal(patch, File.ReadAllBytes(source));
        Assert.Equal(patch.Length - 16, new FileInfo(destination).Length);
        Assert.Equal(File.ReadAllBytes(source + ".stream"), File.ReadAllBytes(destination + ".stream"));
        Assert.Equal(File.ReadAllBytes(source + ".gpu_resources"), File.ReadAllBytes(destination + ".gpu_resources"));
        await Assert.ThrowsAsync<IOException>(() => engine.RepairFileAsync(source, destination));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RepairFileAsync(source, source));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    [Fact]
    public async Task BatchSeparatesErrorsAndSkipsCompanions()
    {
        using var source = new TemporaryDirectory(); using var stage = new TemporaryDirectory();
        var good = Path.Combine(source.Path, Fixtures.ArchiveName + ".patch_0");
        File.WriteAllBytes(good, Fixtures.Patch((1, Fixtures.OtherType, new byte[] { 1 })));
        File.WriteAllBytes(good + ".stream", [2]);
        File.WriteAllBytes(Path.Combine(source.Path, Fixtures.ArchiveName + ".patch_1"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(source.Path, "notes.patch_backup"), [3]);
        var engine = new Repatcher(new UnitSource());
        var result = await engine.RepairFolderAsync(source.Path, stage.Path);
        Assert.Equal(2, result.PatchesFound); Assert.True(result.HasErrors);
        Assert.Contains(result.Files, f => f.Status == RepairStatus.NoUnits);
        Assert.Contains(result.Files, f => f.Status == RepairStatus.Corrupted);
        Assert.False(File.Exists(Path.Combine(stage.Path, Fixtures.ArchiveName + ".patch_1")));
        await Assert.ThrowsAsync<ArgumentException>(() => engine.RepairFolderAsync(source.Path, Path.Combine(source.Path, "stage")));
    }

    [Fact]
    public async Task CancellationLeavesNoOutput()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, Fixtures.ArchiveName + ".patch_0");
        File.WriteAllBytes(source, Fixtures.Patch());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var engine = new Repatcher(new UnitSource());
        Assert.Throws<OperationCanceledException>(() => engine.RepairPatch(File.ReadAllBytes(source), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RepairFileAsync(source, source + ".out", cancellation.Token));
        Assert.False(File.Exists(source + ".out"));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("dsar")]
    [InlineData("slim")]
    public void ReadsInstalledUnitsFromAllArchiveLayoutsAndRepairs(string layout)
    {
        using var game = new TemporaryDirectory();
        var unit = Fixtures.Unit(24, fill: 0xee);
        var archive = Fixtures.Patch((ulong.MaxValue, PatchTable.UnitTypeId, unit));
        if (layout == "slim") Fixtures.Slim(game.Path, archive);
        else File.WriteAllBytes(Path.Combine(game.Path, Fixtures.ArchiveName), layout == "dsar" ? Fixtures.Dsar(archive) : archive);
        var archives = GameArchives.Open(game.Path);
        Assert.Equal(1, archives.UnitCount);
        Assert.Equal(layout == "slim", archives.IsSlim);
        Assert.Equal(unit, archives.ReadUnit(ulong.MaxValue));
        // Readers hold no writable handles; concurrent independent reads are supported.
        Parallel.For(0, 8, _ => Assert.Equal(unit, archives.ReadUnit(ulong.MaxValue)));
        var result = new Repatcher(archives).RepairPatch(Fixtures.Patch((ulong.MaxValue, PatchTable.UnitTypeId, Fixtures.Unit(8))));
        Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Equal(unit, Payload(result.Data!, PatchTable.Read(result.Data!).Resources[0]));
        if (layout == "legacy") Assert.Equal(archive, File.ReadAllBytes(Path.Combine(game.Path, Fixtures.ArchiveName)));
    }

    [Fact]
    public void RejectsInvalidGameDirectoryAndDsar()
    {
        using var temp = new TemporaryDirectory();
        Assert.False(GameArchives.IsValidDataDirectory(temp.Path));
        Assert.Throws<DirectoryNotFoundException>(() => GameArchives.Open(temp.Path));
        var data = Fixtures.Dsar(Fixtures.Patch((1, PatchTable.UnitTypeId, Fixtures.Unit(8))));
        data[32 + 24] = 7;
        File.WriteAllBytes(Path.Combine(temp.Path, Fixtures.ArchiveName), data);
        Assert.Throws<InvalidDataException>(() => GameArchives.Open(temp.Path));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("lz4")]
    public void RejectsMalformedCompressedArchives(string corruption)
    {
        using var temp = new TemporaryDirectory();
        var data = Fixtures.Dsar(Fixtures.Patch((1, PatchTable.UnitTypeId, Fixtures.Unit(8))));
        if (corruption == "truncated") Array.Resize(ref data, 40);
        else
        {
            var offset = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(40));
            data.AsSpan(offset, 6).Clear();
        }
        File.WriteAllBytes(Path.Combine(temp.Path, Fixtures.ArchiveName), data);
        Assert.Throws<InvalidDataException>(() => GameArchives.Open(temp.Path));
    }

    [Fact]
    public async Task RefusesOrphanedStagingCompanions()
    {
        using var temp = new TemporaryDirectory();
        var source = Path.Combine(temp.Path, Fixtures.ArchiveName + ".patch_0");
        var destination = source + ".out";
        File.WriteAllBytes(source, Fixtures.Patch());
        File.WriteAllBytes(destination + ".stream", [0xaa]);
        await Assert.ThrowsAsync<IOException>(() => new Repatcher(new UnitSource()).RepairFileAsync(source, destination));
        Assert.False(File.Exists(destination));
        Assert.Equal(new byte[] { 0xaa }, File.ReadAllBytes(destination + ".stream"));
    }

    [Fact]
    public void SlimCatalogSupportsVariableLengthGroupsAndCompanions()
    {
        using var game = new TemporaryDirectory();
        var unit = Fixtures.Unit(8);
        Fixtures.Slim(game.Path, Fixtures.Patch((1, PatchTable.UnitTypeId, unit)));
        File.WriteAllBytes(Path.Combine(game.Path, "bundle_database.data"), Fixtures.BundleDatabase(
            [Fixtures.ArchiveName, Fixtures.ArchiveName + ".stream", Fixtures.ArchiveName + ".gpu_resources"],
            ["0123456789abcdef"]));
        var archives = GameArchives.Open(game.Path);
        Assert.Equal(unit, archives.ReadUnit(1));
        Assert.Equal(new ulong[] { 1 }, archives.UnitIds);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("length")]
    [InlineData("trailing")]
    public void RejectsMalformedSlimCatalog(string corruption)
    {
        using var game = new TemporaryDirectory();
        Fixtures.Slim(game.Path, Fixtures.Patch((1, PatchTable.UnitTypeId, Fixtures.Unit(8))));
        var path = Path.Combine(game.Path, "bundle_database.data");
        var database = File.ReadAllBytes(path);
        switch (corruption)
        {
            case "version": Fixtures.U32(database, 0, 99); break;
            case "truncated": Array.Resize(ref database, database.Length - 1); break;
            case "length": Fixtures.U32(database, 12, uint.MaxValue); break;
            case "trailing": Array.Resize(ref database, database.Length + 1); break;
        }
        File.WriteAllBytes(path, database);
        Assert.Throws<InvalidDataException>(() => GameArchives.Open(game.Path));
    }
}
