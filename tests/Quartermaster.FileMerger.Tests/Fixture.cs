using System.Buffers.Binary;
using System.Text;

namespace Quartermaster.FileMerger.Tests;

internal sealed class Fixture : IDisposable
{
    public const string Target = "data/game/generated_entities.dl_bin";
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "quartermaster-filemerge-tests-" + Guid.NewGuid().ToString("N"));
    public string Game => Path.Combine(Root, "game");
    public string State => Path.Combine(Root, "state");
    public byte[] Plain { get; } = Config();
    public byte[] Stock { get; }
    public Fixture()
    {
        Stock = GameConfigCodec.Encrypt(Plain);
        Directory.CreateDirectory(Path.Combine(Game, "data", "game"));
        File.WriteAllBytes(Path.Combine(Game, Target), Stock);
    }
    public FileMod Mod(string name, int offset, uint value, string target = Target)
    {
        var bytes = Plain.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + offset), value);
        var sealedBytes = GameConfigCodec.EncryptPreservingHash(bytes, ConfigHash.Compute(Plain));
        return new(name, target, sealedBytes, name + ".patch_0", [FileModPaths.Fingerprint(Stock)]);
    }
    public static byte[] Config(int firstSize = 64, uint firstValue = 100)
    {
        var bytes = new byte[4 + 24 + firstSize + 24 + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 2);
        Block(4, 0x10203040, firstSize);
        Block(28 + firstSize, 0x50607080, 32);
        for (var offset = 0; offset < firstSize; offset += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28 + offset), firstValue + (uint)offset);
        return bytes;
        void Block(int offset, uint type, int size)
        {
            "LDLD\x01\0\0\0"u8.CopyTo(bytes.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 8), type);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 12), (uint)size);
            bytes[offset + 16] = 1;
        }
    }
    public static byte[] DamageConfig(params (uint Id, uint Value)[] records)
    {
        var bytes = new byte[4 + 24 + 16 + records.Length * 76];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        "LDLD\x01\0\0\0"u8.CopyTo(bytes.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0xe0a72cf0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)(bytes.Length - 28));
        bytes[20] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), (uint)records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44 + i * 76), records[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48 + i * 76), records[i].Value);
        }
        return bytes;
    }
    public byte[] Payload(FileMod mod)
    {
        var meta = Encoding.UTF8.GetBytes($"name={mod.Name}\nversion=1\ntarget={mod.Target}\nsize={mod.Content.Length}\nstock={FileModPaths.Fingerprint(Stock)}\nmod={FileModPaths.Fingerprint(mod.Content.Span)}\n");
        using var output = new MemoryStream(); output.Write("HD2FM1"u8);
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(meta.Length); writer.Write(meta); writer.Write(mod.Content.Span);
        return output.ToArray();
    }
    public static byte[] Patch(byte[] content, bool wrapper = true)
    {
        var bytes = new byte[192 + content.Length + (wrapper ? 8 : 0)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4026531857);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(80), 0xa14e8dfa2cd117e2); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104), 0xb5b0c0ffee5b5b11); BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), 0xa14e8dfa2cd117e2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), 192);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(160), (uint)(bytes.Length - 192));
        if (wrapper) { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(192), (uint)content.Length); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(196), 2); }
        content.CopyTo(bytes, wrapper ? 200 : 192); return bytes;
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
