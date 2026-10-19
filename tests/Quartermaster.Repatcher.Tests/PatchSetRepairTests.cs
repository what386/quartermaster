using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;
using Xunit;

namespace Quartermaster.Repatcher.Tests;

public class PatchSetRepairTests
{
    private const ulong Arm = 3234351389789779868; // SR-24 Street Scout, slim right arm (pinned mapping).
    private const ulong Body = 15953443261987891907;
    private static byte[] Payload(PatchSetData set, ulong id) => PatchDocument.Read(set).Resources.Single(r => r.Record.FileId == id).Data.ToArray();

    public static IEnumerable<object[]> RustCases(string section)
    {
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "repatcher-rust-reference.json")));
        foreach (var item in json.RootElement.GetProperty(section).EnumerateArray()) yield return [item.GetProperty("name").GetString()!, item.Clone()];
    }

    [Theory]
    [MemberData(nameof(RustCases), "units")]
    public void MatchesRustEquipmentSafeUnitUpdates(string name, System.Text.Json.JsonElement fixture)
    {
        Assert.NotEmpty(name);
        var old = Convert.FromHexString(fixture.GetProperty("mod").GetString()!);
        var game = Convert.FromHexString(fixture.GetProperty("game").GetString()!);
        var input = Set((Arm, PatchTable.UnitTypeId, old, [], []));
        var result = new Repatcher(new UnitSource((Arm, game))).RepairSet(input);
        Assert.NotNull(result.Output);
        Assert.Equal(Convert.FromHexString(fixture.GetProperty("preserved").GetString()!), Payload(result.Output!, Arm));
    }

    [Theory]
    [MemberData(nameof(RustCases), "culling")]
    public void MatchesRustCullingComposition(string name, System.Text.Json.JsonElement fixture)
    {
        Assert.NotEmpty(name);
        byte[] Bytes(string key) => Convert.FromHexString(fixture.GetProperty(key).GetString()!);
        var input = Set((Arm, PatchTable.UnitTypeId, Bytes("patch"), Bytes("patchGpu"), []));
        var game = new FullSource(Set((Arm, PatchTable.UnitTypeId, Bytes("game"), Bytes("gameGpu"), [])));
        var result = new Repatcher(game, new(Culling: CullingPolicy.CurrentGameArms)).RepairSet(input);
        Assert.NotNull(result.Output);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "CullingFallback");
        var payload = PatchDocument.Read(result.Output!).Resources.Single();
        Assert.Equal(Bytes("expected"), payload.Data.ToArray());
        Assert.Equal(Bytes("expectedGpu"), payload.Gpu.ToArray());
    }

    internal static PatchSetData Set(params (ulong Id, ulong Type, byte[] Data, byte[] Gpu, byte[] Stream)[] resources)
    {
        var main = Fixtures.Patch(resources.Select(r => (r.Id, r.Type, r.Data)).ToArray());
        var table = PatchTable.Read(main);
        using var gpu = new MemoryStream(); using var stream = new MemoryStream();
        for (var i = 0; i < resources.Length; i++)
        {
            var row = table.ItemTableOffset + i * 80;
            Fixtures.U64(main, row + 24, (ulong)stream.Position); Fixtures.U64(main, row + 32, (ulong)gpu.Position);
            Fixtures.U32(main, row + 60, (uint)resources[i].Stream.Length); Fixtures.U32(main, row + 64, (uint)resources[i].Gpu.Length);
            gpu.Write(resources[i].Gpu); stream.Write(resources[i].Stream);
        }
        return new(main, gpu.ToArray(), stream.ToArray());
    }

    [Fact]
    public void DefaultsUpgradeLayoutsAndPreserveCustomLodsRuntimeIdentityAndMissingUnits()
    {
        var old = Fixtures.Unit(24, 1, 0x11);
        Fixtures.U64(old, 8, 0x12345678);
        var input = Set((Arm, PatchTable.UnitTypeId, old, [1, 2], [3, 4]),
            (777, PatchTable.UnitTypeId, Fixtures.Unit(8), [5], [6]),
            (9, Fixtures.OtherType, [7], [8], [9]));
        var engine = new Repatcher(new UnitSource((Arm, Fixtures.Unit(8, 0xA4CD40, 0x22))));
        var result = engine.RepairSet(input);
        Assert.NotNull(result.Output);
        Assert.True(result.Changed); Assert.Equal(1, result.RepairedUnits); Assert.Equal(0, result.RemovedUnits);
        var unit = Payload(result.Output!, Arm);
        Assert.Equal(old.AsSpan(0, 0x28).ToArray(), unit.AsSpan(0, 0x28).ToArray());
        Assert.Equal(old.AsSpan(0x80, 24).ToArray(), unit.AsSpan(0x80, 24).ToArray());
        Assert.Equal(0xA4CD40u, Fixtures.Read32(unit, 0x2c));
        Assert.Equal(21u, Fixtures.Read32(unit, (int)Fixtures.Read32(unit, 0x5c) + 20));
        Assert.Contains(result.Warnings, w => w.Code == "MissingUnitKept");
        Assert.Contains(result.Equipment, e => e.Contains("SR-24"));
        Assert.Contains(result.ArmorAudits, a => a.PresentRoles == 1 && a.MissingRoles.Count == 9);
        Assert.Equal(2, result.Audit!.CheckedUnits); Assert.Equal(0, result.Audit.ChangedHeaders);
        Assert.Equal(input.Gpu!.Value.ToArray(), result.Output!.Gpu!.Value.ToArray());
        Assert.Equal(input.Stream!.Value.ToArray(), result.Output!.Stream!.Value.ToArray());
        Assert.False(engine.RepairSet(result.Output).Changed);
    }

    [Theory]
    [InlineData(MissingUnitPolicy.Keep, true, 0)]
    [InlineData(MissingUnitPolicy.Drop, true, 1)]
    [InlineData(MissingUnitPolicy.Fail, false, 0)]
    public void MissingUnitPolicyIsExplicit(MissingUnitPolicy policy, bool succeeds, int removed)
    {
        var input = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(8), [], []));
        var result = new Repatcher(new UnitSource(), new(MissingUnits: policy)).RepairSet(input);
        Assert.Equal(succeeds, result.Output is not null); Assert.Equal(removed, result.RemovedUnits);
        if (policy == MissingUnitPolicy.Keep) Assert.False(result.Changed);
        if (policy == MissingUnitPolicy.Drop) Assert.Empty(PatchDocument.Read(result.Output!).Resources);
    }

    [Fact]
    public void UnknownEquipmentPreservesLodsAndNewerFormatsAreRejected()
    {
        var input = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(24, 0xA4CD36, 0x11), [], []));
        var result = new Repatcher(new UnitSource((1, Fixtures.Unit(8, 0xA4CD40)))).RepairSet(input);
        Assert.NotNull(result.Output); Assert.Contains(result.Warnings, w => w.Code == "UnclassifiedLodPreserved");
        Assert.Equal(0x98u, Fixtures.Read32(Payload(result.Output!, 1), 0x34));
        Assert.Null(new Repatcher(new UnitSource((1, Fixtures.Unit(8, 1)))).RepairSet(input).Output);
    }

    [Theory]
    [InlineData("gpu")]
    [InlineData("stream")]
    [InlineData("duplicate")]
    [InlineData("overlap")]
    public void RejectsInvalidCompletePatchSet(string corruption)
    {
        var input = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(8), [1], [2]), (2, Fixtures.OtherType, [3], [], []));
        var main = input.Main.ToArray(); var table = PatchTable.Read(main);
        switch (corruption)
        {
            case "gpu": Fixtures.U32(main, table.ItemTableOffset + 64, 2); break;
            case "stream": Fixtures.U64(main, table.ItemTableOffset + 24, ulong.MaxValue); break;
            case "duplicate":
                Fixtures.U64(main, table.ItemTableOffset + 80, 1); Fixtures.U64(main, table.ItemTableOffset + 88, PatchTable.UnitTypeId);
                Fixtures.U64(main, 88, 2); Fixtures.U64(main, 120, 0); break;
            case "overlap": Fixtures.U64(main, table.ItemTableOffset + 96, table.Resources[0].DataOffset); break;
        }
        var result = new Repatcher(new UnitSource((1, Fixtures.Unit(8)))).RepairSet(input with { Main = main });
        Assert.Null(result.Output); Assert.NotEmpty(result.Error!);
    }

    [Fact]
    public void RecoversUniqueLegacyGapWithNonsequentialHeadersAndPreservesEveryPayload()
    {
        var old = Fixtures.Unit(8, 1);
        var input = Set((1, PatchTable.UnitTypeId, old, [1, 2], [3]),
            (2, Fixtures.OtherType, [8, 9, 10], [4], [5, 6]),
            (3, PatchTable.UnitTypeId, Fixtures.Unit(24), [7], [8]));
        var main = input.Main.ToArray(); var table = PatchTable.Read(main);
        // Corrupt one offset, then reorder rows independently of payload order.
        Fixtures.U64(main, table.ItemTableOffset + 16, ulong.MaxValue);
        var row = main.AsSpan(table.ItemTableOffset, 80).ToArray();
        main.AsSpan(table.ItemTableOffset + 160, 80).CopyTo(main.AsSpan(table.ItemTableOffset));
        row.CopyTo(main, table.ItemTableOffset + 160);
        var engine = new Repatcher(new UnitSource((1, Fixtures.Unit(24)), (3, Fixtures.Unit(24))), new(Culling: CullingPolicy.CurrentGameArms));
        var result = engine.RepairSet(input with { Main = main });
        Assert.NotNull(result.Output); Assert.Contains(result.Warnings, w => w.Code == "LegacyRebuilt");
        Assert.Contains(result.Warnings, w => w.Code == "CullingFallback");
        var before = PatchDocument.Read(input); var after = PatchDocument.Read(result.Output!);
        foreach (var r in before.Resources)
        {
            var updated = after.Resources.Single(a => a.Record.FileId == r.Record.FileId);
            Assert.Equal(r.Gpu.ToArray(), updated.Gpu.ToArray()); Assert.Equal(r.Stream.ToArray(), updated.Stream.ToArray());
            Assert.Equal(r.Record.Unknown1, updated.Record.Unknown1);
        }
        Assert.Equal(new byte[] { 8, 9, 10 }, Payload(result.Output!, 2));
        Assert.False(engine.RepairSet(result.Output!).Changed);
        Assert.Null(new Repatcher(new UnitSource(), new(RecoverLegacy: false)).RepairSet(input with { Main = main }).Output);
    }

    [Fact]
    public void RejectsAmbiguousLegacyRecovery()
    {
        var input = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(8), [], []), (2, Fixtures.OtherType, new byte[1], [], []),
            (3, PatchTable.UnitTypeId, Fixtures.Unit(8), [], []));
        var main = input.Main.ToArray(); var table = PatchTable.Read(main);
        Fixtures.U64(main, table.ItemTableOffset + 16, ulong.MaxValue);
        Fixtures.U64(main, table.ItemTableOffset + 176, ulong.MaxValue);
        var result = new Repatcher(new UnitSource()).RepairSet(input with { Main = main });
        Assert.Null(result.Output); Assert.Contains("ambiguous", result.Error!);
    }

    [Fact]
    public void RoundTripPreservesUnknownMetadataAndEmptyCompanionPresence()
    {
        var input = Set((1, Fixtures.OtherType, [1, 2], [3], [4]), (2, PatchTable.UnitTypeId, Fixtures.Unit(8), [5], [6]));
        var doc = PatchDocument.Read(input); var output = doc.Rebuild(doc.Resources);
        var roundTrip = PatchDocument.Read(output);
        Assert.Equal(0x99, output.Main.Span[20]);
        for (var i = 0; i < doc.Resources.Count; i++)
        {
            Assert.Equal(doc.Resources[i].Data.ToArray(), roundTrip.Resources[i].Data.ToArray());
            Assert.Equal(doc.Resources[i].Gpu.ToArray(), roundTrip.Resources[i].Gpu.ToArray());
            Assert.Equal(doc.Resources[i].Record.Unknown4, roundTrip.Resources[i].Record.Unknown4);
            Assert.Equal(0UL, roundTrip.Resources[i].Record.GpuOffset % 64);
        }
        var empty = new PatchSetData(Set((1, Fixtures.OtherType, [1], [], [])).Main);
        var emptyDoc = PatchDocument.Read(empty);
        Assert.Null(emptyDoc.Rebuild(emptyDoc.Resources).Gpu);
        Assert.Null(emptyDoc.Rebuild(emptyDoc.Resources).Stream);
    }

    [Fact]
    public void OptionalCullingReplacesOnlyCanonicalArmsAndImportsDependencies()
    {
        var patch = CullingUnit([false, true], 1, -1);
        var target = CullingUnit([false, true, true], 2, 0);
        var input = Set((Arm, PatchTable.UnitTypeId, patch, [1, 2, 3, 4, 5, 6, 7, 8], [5]),
            (Body, PatchTable.UnitTypeId, patch, [9, 10, 11, 12, 13, 14, 15, 16], [6]));
        var source = new FullSource(Set((Arm, PatchTable.UnitTypeId, target, [21, 22, 23, 24, 25, 26, 27, 28], []),
            (Body, PatchTable.UnitTypeId, target, [31, 32, 33, 34, 35, 36, 37, 38], [])));
        var result = new Repatcher(source, new(Culling: CullingPolicy.CurrentGameArms)).RepairSet(input);
        Assert.NotNull(result.Output); Assert.DoesNotContain(result.Warnings, w => w.Code == "CullingFallback");
        Assert.Contains(result.Units, u => u.UnitId == Arm && u.CullingUpdated);
        var unit = Payload(result.Output!, Arm); var mesh = (int)Fixtures.Read32(unit, 0x64);
        Assert.Equal(3u, Fixtures.Read32(unit, mesh));
        var cull = mesh + (int)Fixtures.Read32(unit, mesh + 8);
        Assert.Equal(1u, Fixtures.Read32(unit, cull + 48)); // imported transform
        Assert.Equal(1u, Fixtures.Read32(unit, cull + 56)); // imported BoneInfo
        Assert.Equal(patch, Payload(result.Output!, Body));
        var outputArm = PatchDocument.Read(result.Output!).Resources.Single(r => r.Record.FileId == Arm);
        Assert.True(outputArm.Gpu.Length > 8); Assert.Equal(new byte[] { 5 }, outputArm.Stream.ToArray());
        Assert.Equal(0, result.Audit!.ChangedHeaders);
    }

    [Fact]
    public void FailedCullingFallsBackWithoutPartiallyChangingOutput()
    {
        var input = Set((Arm, PatchTable.UnitTypeId, CullingUnit([false, true], 1, -1), new byte[8], []));
        var target = CullingUnit([false, true], 1, 0);
        var mesh = (int)Fixtures.Read32(target, 0x64);
        var cull = mesh + (int)Fixtures.Read32(target, mesh + 8);
        Fixtures.U32(target, cull + 48, 999);
        var source = new FullSource(Set((Arm, PatchTable.UnitTypeId, target, new byte[8], [])));
        var result = new Repatcher(source, new(Culling: CullingPolicy.CurrentGameArms)).RepairSet(input);
        Assert.NotNull(result.Output); Assert.Contains(result.Warnings, w => w.Code == "CullingFallback");
        Assert.False(result.Changed); Assert.Equal(input.Main.ToArray(), result.Output!.Main.ToArray());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("dsar")]
    [InlineData("slim")]
    public void ArchiveResourceAccessReadsCompanionsAcrossAllLayouts(string layout)
    {
        using var folder = new TemporaryDirectory();
        var input = Set((Arm, PatchTable.UnitTypeId, Fixtures.Unit(8), [1, 2, 3, 4], [5, 6, 7, 8]),
            (2, Fixtures.OtherType, [9], [10, 11], [12, 13]));
        (string Name, byte[] Data)[] packages = [(Fixtures.ArchiveName, input.Main.ToArray()),
            (Fixtures.ArchiveName + ".gpu_resources", input.Gpu!.Value.ToArray()),
            (Fixtures.ArchiveName + ".stream", input.Stream!.Value.ToArray())];
        if (layout == "slim") Fixtures.SlimPackages(folder.Path, packages);
        else foreach (var (name, bytes) in packages)
            File.WriteAllBytes(Path.Combine(folder.Path, name), layout == "dsar" ? Fixtures.Dsar(bytes) : bytes);
        var source = GameArchives.Open(folder.Path);
        Assert.Equal(2, source.ReadArchiveResources(Fixtures.ArchiveName).Count);
        var resource = source.ReadUnitPayload(Arm);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, resource.Gpu.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, resource.Stream.ToArray());
        var other = source.ReadResourcePayload(Fixtures.ArchiveName, 2, Fixtures.OtherType);
        Assert.Equal(new byte[] { 9 }, other.Data.ToArray());
        Assert.Equal(new byte[] { 10, 11 }, other.Gpu.ToArray());
        Assert.Equal(new byte[] { 12, 13 }, other.Stream.ToArray());
    }

    [Fact]
    public void FallbackEquipmentClassificationUsesReferenceFingerprint()
    {
        var old = Fixtures.Unit(8, 1);
        Fixtures.U64(old, 8, 101); Fixtures.U64(old, 0x20, 102);
        var input = Set((1, PatchTable.UnitTypeId, old, [], []));
        var installed = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(24), [], []));
        var evidence = PatchTable.Read(Set((101, Fixtures.OtherType, [1], [], []), (102, Fixtures.OtherType, [2], [], [])).Main.Span).Resources;
        var source = new FullSource(installed, new Dictionary<string, IReadOnlyList<ResourceRecord>> { ["082f22454170efd6"] = evidence });
        var result = new Repatcher(source).RepairSet(input);
        Assert.NotNull(result.Output); Assert.Contains("Armor: SR-24 Street Scout", result.Equipment);
        Assert.DoesNotContain(result.Warnings, w => w.Code == "UnclassifiedLodPreserved");
    }

    [Fact]
    public async Task CancellationAndFailedValidationLeaveNoStagedFiles()
    {
        using var folder = new TemporaryDirectory();
        var path = Path.Combine(folder.Path, Fixtures.ArchiveName + ".patch_0");
        var input = Set((1, PatchTable.UnitTypeId, Fixtures.Unit(8), [1], []));
        File.WriteAllBytes(path, input.Main.ToArray()); // Required GPU companion is missing.
        var output = Path.Combine(folder.Path, "stage", Path.GetFileName(path));
        var engine = new Repatcher(new UnitSource((1, Fixtures.Unit(24))));
        var result = await engine.RepairFileAsync(path, output);
        Assert.NotEmpty(result.Error!);
        Assert.Equal(RepairStatus.Corrupted, result.Status); Assert.False(File.Exists(output));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.RepairSet(input, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RepairFileAsync(path, output, cancellation.Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Fact]
    public async Task FileRepairPublishesRebuiltCompanionsAndPreservesImportedOriginals()
    {
        using var folder = new TemporaryDirectory();
        var input = Set((Arm, PatchTable.UnitTypeId, CullingUnit([false, true], 1, -1), new byte[8], [7]));
        var game = new FullSource(Set((Arm, PatchTable.UnitTypeId, CullingUnit([false, true, true], 2, 0), new byte[8], [])));
        var engine = new Repatcher(game, new(Culling: CullingPolicy.CurrentGameArms));
        var expected = engine.RepairSet(input).Output!;
        var source = Path.Combine(folder.Path, Fixtures.ArchiveName + ".patch_0");
        File.WriteAllBytes(source, input.Main.ToArray());
        File.WriteAllBytes(source + ".gpu_resources", input.Gpu!.Value.ToArray());
        File.WriteAllBytes(source + ".stream", input.Stream!.Value.ToArray());
        var destination = Path.Combine(folder.Path, "stage", Path.GetFileName(source));
        var result = await engine.RepairFileAsync(source, destination);
        Assert.True(result.Changed); Assert.Equal(RepairStatus.Updated, result.Status);
        Assert.Equal(expected.Main.ToArray(), File.ReadAllBytes(destination));
        Assert.Equal(expected.Gpu!.Value.ToArray(), File.ReadAllBytes(destination + ".gpu_resources"));
        Assert.Equal(expected.Stream!.Value.ToArray(), File.ReadAllBytes(destination + ".stream"));
        Assert.Equal(input.Main.ToArray(), File.ReadAllBytes(source));
        Assert.Equal(input.Gpu.Value.ToArray(), File.ReadAllBytes(source + ".gpu_resources"));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destination)!, "*.tmp"));
    }

    internal static byte[] CullingUnit(bool[] culling, uint transformHash, int cullingBone)
    {
        static byte[] Records(byte[][] records, int absolute, bool trailer)
        {
            var header = 4 + records.Length * 8 + (trailer ? 4 : 0);
            var start = (absolute + header + 15) / 16 * 16 - absolute;
            var bytes = new byte[start + records.Sum(r => r.Length)]; Fixtures.U32(bytes, 0, (uint)records.Length);
            for (var i = 0; i < records.Length; i++)
            {
                Fixtures.U32(bytes, 4 + i * 4, (uint)start); Fixtures.U32(bytes, 4 + records.Length * 4 + i * 4, (uint)i);
                records[i].CopyTo(bytes, start); start += records[i].Length;
            }
            return bytes;
        }
        var stream = new byte[448]; Fixtures.U32(stream, 420, 4); Fixtures.U32(stream, 424, 4); Fixtures.U32(stream, 428, 4);
        var streams = Records([stream], 0x140, true); var meshOffset = 0x140 + streams.Length;
        var meshes = Records(culling.Select(c =>
        {
            var bytes = new byte[156]; Fixtures.U32(bytes, 104, 1); Fixtures.U32(bytes, 108, 128);
            Fixtures.U32(bytes, 120, 1); Fixtures.U32(bytes, 124, 132); Fixtures.U32(bytes, 128, c ? 9u : 7u);
            Fixtures.U32(bytes, 56, c ? unchecked((uint)cullingBone) : uint.MaxValue); return bytes;
        }).ToArray(), meshOffset, false);
        var materials = meshOffset + meshes.Length; var ending = materials + 16; var data = new byte[ending + 8];
        Fixtures.U32(data, 0x2c, 0xA4CD40); Fixtures.U32(data, 0x30, 0x74);
        Fixtures.U32(data, 0x34, 0x80); Fixtures.U32(data, 0x58, 0x120); Fixtures.U32(data, 0x5c, 0x140);
        Fixtures.U32(data, 0x64, (uint)meshOffset); Fixtures.U32(data, 0x70, (uint)materials); Fixtures.U32(data, 0x60, (uint)ending);
        Fixtures.U32(data, 0x80, 1); Fixtures.U32(data, 0x114, transformHash);
        Fixtures.U32(data, 0x120, 1); Fixtures.U32(data, 0x124, 8); Fixtures.U32(data, 0x128, 1); Fixtures.U32(data, 0x130, 16);
        streams.CopyTo(data, 0x140); meshes.CopyTo(data, meshOffset);
        Fixtures.U32(data, materials, 1); Fixtures.U32(data, materials + 4, 7); Fixtures.U64(data, materials + 8, 77);
        Fixtures.U64(data, ending, (ulong)culling.Length); return data;
    }

    private sealed class FullSource(PatchSetData data, IReadOnlyDictionary<string, IReadOnlyList<ResourceRecord>>? archives = null) : IGameResourceSource
    {
        private readonly PatchDocument document = PatchDocument.Read(data);
        public bool ContainsUnit(ulong unitId) => document.Resources.Any(r => r.Record.FileId == unitId);
        public byte[] ReadUnit(ulong unitId, CancellationToken cancellationToken = default) => ReadUnitPayload(unitId, cancellationToken).Data.ToArray();
        public ResourcePayload ReadUnitPayload(ulong unitId, CancellationToken cancellationToken = default) => document.Resources.Single(r => r.Record.FileId == unitId);
        public IReadOnlyList<ResourceRecord> ReadArchiveResources(string archive, CancellationToken cancellationToken = default) =>
            archives is not null && archives.TryGetValue(archive, out var records) ? records : [];
    }
}
