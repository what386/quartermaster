using System.Buffers.Binary;
using Xunit;

namespace Quartermaster.FileMerger.Tests;

public sealed class StructuredMergerTests
{
    private const uint Damage = 0xe0a72cf0, Recharge = 0x1f42878e, Passive = 0x63ce0feb, Unknown = 0x10203040;
    private static uint U32(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
    private static ulong U64(ReadOnlySpan<byte> bytes, int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]);
    private static void Put(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
    private static void Put64(byte[] bytes, int at, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
    private static byte[] Record(uint id, uint value, uint other = 10, int size = 76)
    {
        var bytes = new byte[size]; Put(bytes, 0, id); Put(bytes, 4, value);
        if (size >= 12) Put(bytes, 8, other);
        return bytes;
    }
    private static byte[] Rows(params byte[][] records)
    {
        var bytes = new byte[16 + records.Length * 76]; Put64(bytes, 0, 0x12345678); Put(bytes, 8, (uint)records.Length);
        for (var i = 0; i < records.Length; i++) records[i].CopyTo(bytes, 16 + i * 76);
        return bytes;
    }
    private static byte[] Keyed(int buckets, params (ulong Id, uint Value)[] records) => KeyedStride(4, buckets, records);
    private static byte[] KeyedStride(int stride, int buckets, params (ulong Id, uint Value)[] records)
    {
        var bytes = new byte[buckets * 16 + records.Length * stride];
        for (var i = 0; i < records.Length; i++)
        {
            var slot = (int)(records[i].Id % (ulong)buckets);
            while (U64(bytes, slot * 16) != 0) slot = (slot + 1) % buckets;
            Put64(bytes, slot * 16, records[i].Id); Put(bytes, slot * 16 + 8, (uint)i);
            Put(bytes, buckets * 16 + i * stride, records[i].Value);
        }
        return bytes;
    }
    private static byte[] Modifier(uint id, uint value, uint description = 0)
    {
        var bytes = Record(id, 1, value, 16); Put(bytes, 12, description); return bytes;
    }
    private static byte[] Armor(uint id, byte[][] modifiers, byte[][] stats)
    {
        var bytes = new byte[56 + modifiers.Length * 16 + stats.Length * 12]; Put(bytes, 0, id); Put(bytes, 4, 99);
        Put64(bytes, 16, modifiers.Length == 0 ? ulong.MaxValue : 56); Put(bytes, 24, (uint)modifiers.Length);
        Put64(bytes, 32, stats.Length == 0 ? ulong.MaxValue : (ulong)(56 + modifiers.Length * 16)); Put(bytes, 40, (uint)stats.Length);
        var at = 56;
        foreach (var record in modifiers.Concat(stats)) { record.CopyTo(bytes, at); at += record.Length; }
        return bytes;
    }
    private static byte[] Config(params (uint Type, byte[] Data)[] blocks)
    {
        using var output = new MemoryStream(); var prefix = new byte[4]; Put(prefix, 0, (uint)blocks.Length); output.Write(prefix);
        foreach (var (type, data) in blocks)
        {
            var header = new byte[24]; "LDLD\x01\0\0\0"u8.CopyTo(header); Put(header, 8, type); Put(header, 12, (uint)data.Length); header[16] = 1;
            output.Write(header); output.Write(data);
        }
        return output.ToArray();
    }
    private static byte[] Payload(byte[] plain, int index = 0)
    {
        var at = 4;
        for (var i = 0; i < index; i++) at += 24 + (int)U32(plain, at + 12);
        return plain.AsSpan(at + 24, (int)U32(plain, at + 12)).ToArray();
    }
    private static FileMod Mod(string name, byte[] baseline, byte[] stock, byte[] plain) =>
        new(name, Fixture.Target, GameConfigCodec.EncryptPreservingHash(plain, ConfigHash.Compute(baseline)), name, [FileModPaths.Fingerprint(stock)]);
    private static Dictionary<uint, byte[]> ReadRows(byte[] plain)
    {
        var payload = Payload(plain);
        return Enumerable.Range(0, (int)U32(payload, 8)).Select(i => payload.AsSpan(16 + i * 76, 76).ToArray()).ToDictionary(row => U32(row, 0));
    }

    [Fact]
    public void GrownRowsMergeIndependentEditsAndShiftedFollowingBlocks()
    {
        var tail = new byte[32]; Put(tail, 0, 50);
        var baseline = Config((Damage, Rows(Record(1, 100), Record(2, 200))), (Unknown, tail)); var stock = GameConfigCodec.Encrypt(baseline);
        var grown = Config((Damage, Rows(Record(1, 100), Record(2, 210), Record(3, 300))), (Unknown, tail));
        var editedTail = tail.ToArray(); Put(editedTail, 4, 777);
        var edited = Config((Damage, Rows(Record(1, 110), Record(2, 200))), (Unknown, editedTail));
        var a = Mod("Add record", baseline, stock, grown); var b = Mod("Edit existing", baseline, stock, edited);
        var saved = a.Content.ToArray(); var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b]);
        var plain = GameConfigCodec.Decrypt(result.Content); var rows = ReadRows(plain);
        Assert.Equal(3, rows.Count); Assert.Equal(110u, U32(rows[1], 4)); Assert.Equal(210u, U32(rows[2], 4)); Assert.Equal(300u, U32(rows[3], 4));
        Assert.Equal(777u, U32(Payload(plain, 1), 4));
        Assert.Equal(ConfigHash.Compute(baseline), ConfigHash.Compute(plain));
        Assert.Equal(saved, a.Content.ToArray()); Assert.Empty(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ReorderedRowsUseStableIdsEvenAtTheSameFileSize()
    {
        var baseline = Config((Damage, Rows(Record(1, 100), Record(2, 200)))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("Reorder", baseline, stock, Config((Damage, Rows(Record(2, 210), Record(1, 100)))));
        var b = Mod("Other edit", baseline, stock, Config((Damage, Rows(Record(1, 110), Record(2, 200)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b]); var rows = ReadRows(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(110u, U32(rows[1], 4)); Assert.Equal(210u, U32(rows[2], 4)); Assert.Empty(result.Conflicts); Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndependentlyAddedRowsAreBothRetained(bool reverse)
    {
        var baseline = Config((Damage, Rows(Record(1, 100)))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("Add A", baseline, stock, Config((Damage, Rows(Record(1, 100), Record(2, 200)))));
        var b = Mod("Add B", baseline, stock, Config((Damage, Rows(Record(1, 100), Record(3, 300)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, reverse ? [b, a] : [a, b]);
        Assert.Equal(new uint[] { 1, 2, 3 }, ReadRows(GameConfigCodec.Decrypt(result.Content)).Keys.Order()); Assert.Empty(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void ConflictingAdditionsUseWholeRecordsAndKeepOtherChanges()
    {
        var baseline = Config((Damage, Rows(Record(1, 100)))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("First", baseline, stock, Config((Damage, Rows(Record(1, 110), Record(2, 200)))));
        var b = Mod("Winner", baseline, stock, Config((Damage, Rows(Record(1, 100), Record(2, 300)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b]); var rows = ReadRows(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(110u, U32(rows[1], 4)); Assert.Equal(300u, U32(rows[2], 4));
        var conflict = Assert.Single(result.Conflicts); Assert.Equal(-1, conflict.Offset); Assert.Equal("record 0000000000000002", conflict.RecordId);
        Assert.Equal("First", conflict.LosingSource); Assert.Equal("Winner", conflict.WinningSource); Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletionAndEditingUsePriorityWithoutAStockCopyUndoingDeletion(bool deleteWins)
    {
        var baseline = Config((Damage, Rows(Record(1, 100), Record(2, 200)))); var stock = GameConfigCodec.Encrypt(baseline);
        var remove = Mod("Remove", baseline, stock, Config((Damage, Rows(Record(1, 100)))));
        var edit = Mod("Edit", baseline, stock, Config((Damage, Rows(Record(1, 100), Record(2, 250)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, deleteWins ? [edit, remove] : [remove, edit]);
        var rows = ReadRows(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(!deleteWins, rows.ContainsKey(2));
        var conflict = Assert.Single(result.Conflicts); Assert.Equal(deleteWins ? "Remove" : "Edit", conflict.WinningMod);
        var unrelated = Mod("Unrelated", baseline, stock, Config((Damage, Rows(Record(1, 110), Record(2, 200)))));
        result = ConfigMerger.Merge(Fixture.Target, stock, [remove, unrelated]); rows = ReadRows(GameConfigCodec.Decrypt(result.Content));
        Assert.False(rows.ContainsKey(2)); Assert.Equal(110u, U32(rows[1], 4)); Assert.Empty(result.Conflicts);
    }

    [Theory]
    [InlineData(100u, 2)]
    [InlineData(200u, 1)]
    public void RestoredRecordsReportOnlyEditsMissingFromFinalOutput(uint earlier, int count)
    {
        var baseline = Config((Damage, Rows(Record(1, 10)))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("Earlier", baseline, stock, Config((Damage, Rows(Record(1, earlier)))));
        var b = Mod("Delete", baseline, stock, Config((Damage, Rows())));
        var c = Mod("Restore", baseline, stock, Config((Damage, Rows(Record(1, 200)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b, c]);
        Assert.Equal(200u, U32(ReadRows(GameConfigCodec.Decrypt(result.Content))[1], 4));
        Assert.Equal(count, result.Conflicts.Count); Assert.All(result.Conflicts, conflict => Assert.Equal("Restore", conflict.WinningMod));
    }

    [Fact]
    public void RestoringADeletedRecordDoesNotResurrectEditsBeforeTheDeletion()
    {
        var baseline = Config((Damage, Rows(Record(1, 100)))); var stock = GameConfigCodec.Encrypt(baseline);
        var first = Mod("Earlier", baseline, stock, Config((Damage, Rows(Record(1, 100, 20)))));
        var remove = Mod("Delete", baseline, stock, Config((Damage, Rows())));
        var restore = Mod("Restore", baseline, stock, Config((Damage, Rows(Record(1, 110)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [first, remove, restore]);
        var record = ReadRows(GameConfigCodec.Decrypt(result.Content))[1];
        Assert.Equal(110u, U32(record, 4)); Assert.Equal(10u, U32(record, 8));
        Assert.Contains(result.Conflicts, conflict => conflict.LosingMod == "Earlier" && conflict.Offset == 8 && conflict.WinningMod == "Restore");
        Assert.Contains(result.Conflicts, conflict => conflict.LosingMod == "Delete" && conflict.Offset == -1 && conflict.WinningMod == "Restore");
    }

    [Fact]
    public void ConflictsAtTheSameFieldOffsetRetainTheirDifferentRecordIdentities()
    {
        var baseline = Config((Damage, Rows(Record(1, 100), Record(2, 200)))); var stock = GameConfigCodec.Encrypt(baseline);
        var first = Mod("First", baseline, stock, Config((Damage, Rows(Record(1, 110), Record(2, 210)))));
        var last = Mod("Last", baseline, stock, Config((Damage, Rows(Record(1, 120), Record(2, 220)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [first, last]);
        Assert.Equal(2, result.Conflicts.Count); Assert.Equal(2, result.Conflicts.Select(conflict => conflict.RecordId).Distinct().Count());
        Assert.All(result.Conflicts, conflict => { Assert.Equal(4, conflict.Offset); Assert.Equal("Last", conflict.WinningSource); });
    }

    [Fact]
    public void KeyedTablesRebuildLookupBucketsWhenGrowingAndRehashing()
    {
        var baseline = Config((Recharge, Keyed(4, (10, 100), (18, 180)))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("Grow", baseline, stock, Config((Recharge, Keyed(8, (18, 180), (10, 100), (99, 990)))));
        var b = Mod("Edit", baseline, stock, Config((Recharge, Keyed(6, (10, 111), (18, 181)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b]); var payload = Payload(GameConfigCodec.Decrypt(result.Content));
        const int buckets = 6;
        foreach (var (key, value) in new[] { (10UL, 111u), (18UL, 181u), (99UL, 990u) })
        {
            var slot = (int)(key % buckets);
            for (var probes = 0; U64(payload, slot * 16) != key; probes++)
            { Assert.True(probes < buckets); Assert.NotEqual(0UL, U64(payload, slot * 16)); slot = (slot + 1) % buckets; }
            Assert.Equal(value, U32(payload, buckets * 16 + (int)U32(payload, slot * 16 + 8) * 4));
        }
        Assert.Empty(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Theory]
    [InlineData(Recharge, 4)]
    [InlineData(0x54270608u, 280)]
    [InlineData(0x14729b6au, 44)]
    [InlineData(0xff67a367u, 24)]
    [InlineData(0x84ce7eeeu, 36)]
    [InlineData(0x32528174u, 64)]
    public void EverySupportedComponentStrideHandlesGrowth(uint type, int stride)
    {
        var baseline = Config((type, KeyedStride(stride, 2, (10, 100)))); var stock = GameConfigCodec.Encrypt(baseline);
        var grow = Mod("Grow", baseline, stock, Config((type, KeyedStride(stride, 4, (10, 100), (20, 200)))));
        var edit = Mod("Edit", baseline, stock, Config((type, KeyedStride(stride, 2, (10, 110)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [grow, edit]);
        var payload = Payload(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(110u, U32(payload, 64)); Assert.Equal(200u, U32(payload, 64 + stride));
        Assert.Empty(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void InvalidKeyedLookupCannotBeRebuiltAsIfItHadStableRecords()
    {
        var baseline = Config((Recharge, Keyed(4, (10, 100), (18, 180)))); var stock = GameConfigCodec.Encrypt(baseline);
        var broken = Keyed(4, (10, 110), (18, 180));
        // Both live hash buckets point at the same row: identity cannot be recovered uniquely.
        for (var slot = 0; slot < 4; slot++) if (U64(broken, slot * 16) != 0) Put(broken, slot * 16 + 8, 0);
        var first = Mod("Other edit", baseline, stock, Config((Recharge, Keyed(4, (10, 100), (18, 190)))));
        var winner = Mod("Broken index", baseline, stock, Config((Recharge, broken)));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [first, winner]);
        Assert.Single(result.Skipped); Assert.Equal(winner.Content.ToArray(), result.Content); Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void KeyedDeletionRebuildsAnEmptyTableWithoutReintroducingUnchangedRecords()
    {
        var baseline = Config((Recharge, Keyed(4, (10, 100), (18, 180)))); var stock = GameConfigCodec.Encrypt(baseline);
        var remove = Mod("Remove both", baseline, stock, Config((Recharge, Keyed(2))));
        var unchanged = Mod("Unchanged", baseline, stock, baseline);
        var result = ConfigMerger.Merge(Fixture.Target, stock, [remove, unchanged]);
        var payload = Payload(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(new byte[32], payload.AsSpan(0, 32).ToArray()); Assert.Empty(result.Conflicts); Assert.Empty(result.Skipped);
    }

    [Fact]
    public void PureKeyedRehashingWithNoRecordChangesKeepsOriginalCiphertext()
    {
        var baseline = Config((Recharge, Keyed(4, (10, 100), (18, 180)))); var stock = GameConfigCodec.Encrypt(baseline);
        var mod = Mod("Rehash", baseline, stock, Config((Recharge, Keyed(8, (18, 180), (10, 100)))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [mod]); Assert.Equal(stock, result.Content); Assert.Empty(result.Skipped);
    }

    [Fact]
    public void ArmorModifierGrowthRepositionsStatsAndKeepsIndependentEdits()
    {
        var baseline = Config((Passive, Armor(1, [Modifier(11, 100)], [Record(5, 1, 50, 12)]))); var stock = GameConfigCodec.Encrypt(baseline);
        var a = Mod("Stack", baseline, stock, Config((Passive, Armor(1, [Modifier(11, 100), Modifier(22, 200)], [Record(5, 1, 50, 12)]))));
        var b = Mod("Stats", baseline, stock, Config((Passive, Armor(1, [Modifier(11, 110)], [Record(5, 1, 55, 12), Record(6, 1, 60, 12)]))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [a, b]); var payload = Payload(GameConfigCodec.Decrypt(result.Content));
        Assert.Equal(2u, U32(payload, 24)); Assert.Equal(2u, U32(payload, 40)); Assert.Equal(56UL, U64(payload, 16)); Assert.Equal(88UL, U64(payload, 32));
        Assert.Equal(110u, U32(payload, 64)); Assert.Equal(200u, U32(payload, 80)); Assert.Equal(55u, U32(payload, 96)); Assert.Equal(60u, U32(payload, 108));
        Assert.Empty(result.Conflicts); Assert.Empty(result.Skipped);
    }

    [Fact]
    public void RepeatedPassiveBlocksRetainIdentityWhenTheFirstGrows()
    {
        var one = Armor(1, [Modifier(11, 100)], []); var two = Armor(2, [Modifier(22, 200)], []);
        var baseline = Config((Passive, one), (Passive, two)); var stock = GameConfigCodec.Encrypt(baseline);
        var grow = Mod("Grow first", baseline, stock, Config((Passive, Armor(1, [Modifier(11, 100), Modifier(33, 300)], [])), (Passive, two)));
        var edit = Mod("Edit second", baseline, stock, Config((Passive, one), (Passive, Armor(2, [Modifier(22, 210)], []))));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [grow, edit]); var plain = GameConfigCodec.Decrypt(result.Content);
        Assert.Equal(1u, U32(Payload(plain), 0)); Assert.Equal(2u, U32(Payload(plain), 24));
        Assert.Equal(2u, U32(Payload(plain, 1), 0)); Assert.Equal(210u, U32(Payload(plain, 1), 64));
        Assert.Empty(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("pointer")]
    [InlineData("identity")]
    public void AmbiguousOrUnsupportedArmorLayoutsRemainExclusive(string failure)
    {
        var baseline = Config((Passive, Armor(1, [Modifier(11, 100)], []))); var stock = GameConfigCodec.Encrypt(baseline);
        var invalid = Armor(1, [Modifier(11, 110), Modifier(failure == "duplicate" ? 11u : 22u, 200)], []);
        if (failure == "pointer") Put64(invalid, 16, 57);
        if (failure == "identity") Put(invalid, 0, 2);
        var first = Mod("First", baseline, stock, Config((Passive, Armor(1, [Modifier(11, 120)], []))));
        var winner = Mod("Unsupported", baseline, stock, Config((Passive, invalid)));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [first, winner]);
        Assert.Equal(winner.Content.ToArray(), result.Content); Assert.Single(result.Skipped); Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void UnsafeCombinedArmorDescriptionCountDoesNotProduceAMergedConfig()
    {
        var baseline = Config((Passive, Armor(1, [Modifier(11, 100, 1)], []))); var stock = GameConfigCodec.Encrypt(baseline);
        var mods = new uint[] { 22, 33, 44 }.Select(id => Mod(id.ToString(), baseline, stock,
            Config((Passive, Armor(1, [Modifier(11, 100, 1), Modifier(id, 200, 1)], []))))).ToArray();
        var result = ConfigMerger.Merge(Fixture.Target, stock, mods);
        Assert.Equal(mods[^1].Content.ToArray(), result.Content); Assert.Equal(2, result.Skipped.Count);
        Assert.Contains("three described modifiers", result.Skipped[0].Reason);
    }

    [Fact]
    public void MovingRecordsBetweenRepeatedBlocksDoesNotDuplicateThemDuringMerge()
    {
        var firstRows = Rows(Record(1, 100)); var secondRows = Rows(Record(2, 200));
        var baseline = Config((Damage, firstRows), (Damage, secondRows)); var stock = GameConfigCodec.Encrypt(baseline);
        var reorder = Mod("Swap blocks", baseline, stock, Config((Damage, secondRows), (Damage, firstRows)));
        var edit = Mod("Edit", baseline, stock, Config((Damage, Rows(Record(1, 110))), (Damage, secondRows)));
        var result = ConfigMerger.Merge(Fixture.Target, stock, [reorder, edit]);
        Assert.Equal(edit.Content.ToArray(), result.Content); Assert.Single(result.Skipped);
        Assert.Contains("block reordering", result.Skipped[0].Reason);
    }

    [Fact]
    public async Task InstallerAppliesSizeChangingMergeAndRestoresExactStock()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Game, Fixture.Target);
        var baseline = Config((Damage, Rows(Record(1, 100)))); var stock = GameConfigCodec.Encrypt(baseline); await File.WriteAllBytesAsync(path, stock);
        var a = Mod("Grow", baseline, stock, Config((Damage, Rows(Record(1, 100), Record(2, 200)))));
        var b = Mod("Edit", baseline, stock, Config((Damage, Rows(Record(1, 110)))));
        var installer = new FileModInstaller(f.State); var plan = await installer.PrepareAsync(f.Game, [a, b]);
        Assert.Empty(plan.Skipped); Assert.Equal(stock, await File.ReadAllBytesAsync(path));
        await installer.ApplyAsync(plan);
        Assert.Equal(110u, U32(ReadRows(GameConfigCodec.Decrypt(await File.ReadAllBytesAsync(path)))[1], 4));
        await installer.RestoreAsync(f.Game); Assert.Equal(stock, await File.ReadAllBytesAsync(path));
    }
}
