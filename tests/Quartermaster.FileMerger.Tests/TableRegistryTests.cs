using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Quartermaster.FileMerger.Tests;

public sealed class TableRegistryTests
{
    private static ConfigTableRegistry Load(string tables) => Read("{\"schemaVersion\":1,\"tables\":[" + tables + "]}");
    private static ConfigTableRegistry Read(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ConfigTableRegistry.Load(stream);
    }
    private const string Table = """{"type":"0x12345678","name":"Test","layout":"id-rows","recordSize":76}""";

    [Fact]
    public void BundledRegistryPreservesAllSupportedLayouts()
    {
        var registry = ConfigTableRegistry.Default;
        Assert.Equal(8, registry.Tables.Count);
        Assert.True(registry.TryGet(0xe0a72cf0, out var damage));
        Assert.Equal(76, Assert.Single(damage.Variants).RecordSize);
        Assert.Equal("Damage", damage.Variants[0].Fields[4]);
        Assert.True(registry.TryGet(0x1f42878e, out var recharge));
        Assert.Equal(ConfigTableKind.Recharge, recharge.Kind); Assert.Empty(recharge.Variants);
        Assert.True(registry.TryGet(0x63ce0feb, out var passive));
        Assert.Equal(ConfigTableKind.Passive, passive.Kind); Assert.Empty(passive.Variants);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":2,\"tables\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1,\"tables\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"tables\":[],\"include\":\"other.json\"}")]
    [InlineData("null")]
    [InlineData("{")]
    public void InvalidDocumentsFailAsAWhole(string json) => Assert.Throws<InvalidDataException>(() => Read(json));

    [Theory]
    [InlineData("\"recordSize\":76", "\"recordSize\":75")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"idOffset\":76")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"pointerOffset\":8")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"maxRecords\":250001")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"fields\":{\"3\":\"Invalid\"}")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"fields\":{\"4\":\"A\",\"04\":\"B\"}")]
    [InlineData("id-rows", "unknown")]
    [InlineData("id-rows", "armor-passive")]
    [InlineData("id-rows", "recharge")]
    [InlineData("0x12345678", "0x00000000")]
    [InlineData("\"recordSize\":76", "\"variants\":[{\"recordSize\":76},{\"recordSize\":76}]")]
    [InlineData("\"recordSize\":76", "\"recordSize\":76,\"variants\":[{\"recordSize\":80}]")]
    public void UnsafeOrAmbiguousDefinitionsAreRejected(string before, string after) =>
        Assert.Throws<InvalidDataException>(() => Load(Table.Replace(before, after)));

    [Fact]
    public void DuplicateIdsAreRejected() => Assert.Throws<InvalidDataException>(() => Load(Table + "," + Table));

    [Fact]
    public void FingerprintTracksRulesButNotPresentationOrOrdering()
    {
        var other = Table.Replace("12345678", "87654321");
        Assert.Equal(Load(Table + "," + other).Fingerprint,
            Load(other + "," + Table.Replace("Test", "Renamed").Replace("\"recordSize\":76", "\"recordSize\":76,\"fields\":{\"4\":\"Value\"}")).Fingerprint);
        Assert.NotEqual(Load(Table).Fingerprint, Load(Table.Replace("76", "80")).Fingerprint);
        Assert.NotEqual(Load(Table).Fingerprint, Load(Table.Replace("\"recordSize\":76", "\"recordSize\":76,\"maxRecords\":10")).Fingerprint);
    }

    [Fact]
    public void OversizedRegistryIsRejectedAndInputStreamRemainsOpen()
    {
        using var oversized = new MemoryStream(new byte[1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => ConfigTableRegistry.Load(oversized));
        Assert.True(oversized.CanRead);
        using var valid = new MemoryStream(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"tables\":[" + Table + "]}"));
        ConfigTableRegistry.Load(valid); Assert.True(valid.CanRead);
    }

    [Fact]
    public void CombinedRecordCountCannotExceedRegistryLimit()
    {
        var registry = Load(Table.Replace("\"recordSize\":76", "\"recordSize\":76,\"maxRecords\":2"));
        var baseline = Rows(0, (1, 100)); var stock = GameConfigCodec.Encrypt(baseline);
        FileMod Mod(string name, uint id) => new(name, Fixture.Target,
            GameConfigCodec.EncryptPreservingHash(Rows(0, (1, 100), (id, 200)), ConfigHash.Compute(baseline)), name, []);
        var mods = new[] { Mod("Add A", 2), Mod("Add B", 3) };
        var result = ConfigMerger.Merge(Fixture.Target, stock, mods, registry: registry);
        Assert.Equal(mods[^1].Content.ToArray(), result.Content);
        Assert.Contains("record limit", Assert.Single(result.Skipped).Reason);
    }

    private static byte[] Rows(int idOffset, params (uint Id, uint Value)[] records)
    {
        var bytes = Fixture.DamageConfig(records);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x12345678);
        if (idOffset == 4)
            for (var i = 0; i < records.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44 + i * 76), records[i].Value);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48 + i * 76), records[i].Id);
            }
        return bytes;
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(4, false)]
    [InlineData(0, true)]
    public async Task CustomRegistryMergesNewTypesAndInstallerUsesTheSameRules(int idOffset, bool variants)
    {
        var definition = Table.Replace("\"recordSize\":76", variants
            ? "\"variants\":[{\"recordSize\":76},{\"recordSize\":80}]"
            : "\"recordSize\":76,\"idOffset\":" + idOffset);
        var registry = Load(definition);
        var baseline = Rows(idOffset, (1, 100)); var stock = GameConfigCodec.Encrypt(baseline);
        FileMod Mod(string name, params (uint Id, uint Value)[] rows) => new(name, Fixture.Target,
            GameConfigCodec.EncryptPreservingHash(Rows(idOffset, rows), ConfigHash.Compute(baseline)), name, [FileModPaths.Fingerprint(stock)]);
        var mods = new[] { Mod("Grow", (1, 100), (2, 200)), Mod("Edit", (1, 110)) };
        Assert.Single(ConfigMerger.Merge(Fixture.Target, stock, mods).Skipped); // Custom definitions never mutate the default registry.
        var merged = ConfigMerger.Merge(Fixture.Target, stock, mods, registry: registry);
        Assert.Empty(merged.Skipped);
        var plain = GameConfigCodec.Decrypt(merged.Content);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(36)));
        Assert.Equal(110u, BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(idOffset == 0 ? 48 : 44)));
        using var f = new Fixture(); var path = Path.Combine(f.Game, Fixture.Target);
        await File.WriteAllBytesAsync(path, stock);
        var installer = new FileModInstaller(f.State, registry);
        var plan = await installer.PrepareAsync(f.Game, mods); Assert.Empty(plan.Skipped);
        await installer.ApplyAsync(plan);
        Assert.Equal(plain, GameConfigCodec.Decrypt(await File.ReadAllBytesAsync(path)));
        await installer.RestoreAsync(f.Game); Assert.Equal(stock, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void AmbiguousVariantsAndRecordLimitsKeepWholeFileFallback()
    {
        var baseline = Rows(0, (1, 100)); var stock = GameConfigCodec.Encrypt(baseline);
        FileMod Mod(string name, params (uint Id, uint Value)[] rows) => new(name, Fixture.Target,
            GameConfigCodec.EncryptPreservingHash(Rows(0, rows), ConfigHash.Compute(baseline)), name, []);
        var mods = new[] { Mod("Grow", (1, 100), (2, 200)), Mod("Edit", (1, 110)) };
        foreach (var rule in new[] { "\"variants\":[{\"recordSize\":76},{\"recordSize\":76,\"idOffset\":4}]", "\"recordSize\":76,\"maxRecords\":1" })
        {
            var result = ConfigMerger.Merge(Fixture.Target, stock, mods, registry: Load(Table.Replace("\"recordSize\":76", rule)));
            Assert.Single(result.Skipped); Assert.Equal(mods[^1].Content.ToArray(), result.Content);
        }
    }
}
