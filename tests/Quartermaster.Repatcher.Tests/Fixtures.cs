using System.Buffers.Binary;
using K4os.Compression.LZ4;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Tests;

internal static class Fixtures
{
    public const string ArchiveName = "9ba626afa44a3aa3";
    public const ulong OtherType = 0x1122334455667788;
    public static void U32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    public static void U64(byte[] data, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), value);
    public static uint Read32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    public static byte[] Unit(int lodSize, uint version = 0xA4CD36, byte fill = 0xdd)
    {
        const int lodOffset = 0x80;
        var jointOffset = lodOffset + lodSize;
        var layoutList = jointOffset + 8;
        var data = new byte[layoutList + 8 + 8 + 16 * 20 + 16];
        U32(data, 0x2c, version);
        U32(data, 0x30, lodOffset);
        U32(data, 0x34, (uint)jointOffset);
        U32(data, 0x38, (uint)(jointOffset + 4));
        U32(data, 0x5c, (uint)layoutList);
        data.AsSpan(lodOffset, lodSize).Fill(fill);
        data.AsSpan(jointOffset, 8).Fill(0x7b);
        U32(data, layoutList, 1);
        U32(data, layoutList + 4, 8);
        for (var i = 0; i < 16; i++)
        {
            U32(data, layoutList + 16 + i * 20, (uint)i);
            U32(data, layoutList + 20 + i * 20, i % 2 == 0 ? 17u : 16u);
        }
        data.AsSpan(data.Length - 16).Fill(0xab);
        return data;
    }

    public static byte[] Patch(params (ulong Id, ulong Type, byte[] Data)[] resources)
    {
        var types = resources.GroupBy(r => r.Type).ToArray();
        var dataStart = 72 + types.Length * 32 + resources.Length * 80;
        var patch = new byte[dataStart + resources.Sum(r => r.Data.Length)];
        U32(patch, 0, PatchTable.Magic);
        U32(patch, 4, (uint)types.Length);
        U32(patch, 8, (uint)resources.Length);
        patch[20] = 0x99;
        for (var i = 0; i < types.Length; i++)
        {
            U64(patch, 72 + 32 * i + 8, types[i].Key);
            U64(patch, 72 + 32 * i + 16, (ulong)types[i].Count());
        }
        var cursor = dataStart;
        for (var i = 0; i < resources.Length; i++)
        {
            var row = 72 + types.Length * 32 + 80 * i;
            var r = resources[i];
            U64(patch, row, r.Id); U64(patch, row + 8, r.Type); U64(patch, row + 16, (ulong)cursor);
            U64(patch, row + 24, 123); U64(patch, row + 32, 456);
            U64(patch, row + 40, 0xfedcba9876543210); U64(patch, row + 48, 42);
            U32(patch, row + 56, (uint)r.Data.Length); U32(patch, row + 60, 20); U32(patch, row + 64, 30);
            U32(patch, row + 68, 91); U32(patch, row + 72, 92); U32(patch, row + 76, (uint)i);
            r.Data.CopyTo(patch, cursor);
            cursor += r.Data.Length;
        }
        return patch;
    }

    public static byte[] Dsar(byte[] data, int chunkSize = 97, bool compress = true)
    {
        var count = (data.Length + chunkSize - 1) / chunkSize;
        using var output = new MemoryStream();
        var table = new byte[32 + count * 32];
        U32(table, 0, 1380012868); U32(table, 8, (uint)count);
        output.Write(table);
        for (var i = 0; i < count; i++)
        {
            var offset = i * chunkSize;
            var length = Math.Min(chunkSize, data.Length - offset);
            byte[] encoded;
            var compressed = compress && i % 2 == 0;
            if (compressed)
            {
                encoded = new byte[LZ4Codec.MaximumOutputSize(length)];
                var size = LZ4Codec.Encode(data, offset, length, encoded, 0, encoded.Length);
                Array.Resize(ref encoded, size);
            }
            else encoded = data.AsSpan(offset, length).ToArray();
            var row = 32 + 32 * i;
            U64(table, row, (ulong)offset); U64(table, row + 8, (ulong)output.Position);
            U32(table, row + 16, (uint)length); U32(table, row + 20, (uint)encoded.Length);
            table[row + 24] = compressed ? (byte)3 : (byte)0;
            table[row + 25] = i == 0 ? (byte)2 : (byte)4;
            output.Write(encoded);
        }
        output.Position = 0; output.Write(table);
        return output.ToArray();
    }

    public static void Slim(string folder, byte[] archive)
    {
        // Split a package across two entries/bundles, in addition to DSAR chunk splitting.
        var split = archive.Length / 2;
        File.WriteAllBytes(Path.Combine(folder, "bundles.00.nxa"), Dsar(archive[..split]));
        File.WriteAllBytes(Path.Combine(folder, "bundles.01.nxa"), Dsar(archive[split..]));
        var index = new byte[24 + 24 + 32 + 17];
        U32(index, 12, 2); U32(index, 16, 1);
        U64(index, 24, (ulong)archive.Length); U32(index, 32, 80); U32(index, 36, 2); U32(index, 40, 48);
        U64(index, 48, 0); U32(index, 56, 0); index[63] = 0;
        U64(index, 64, (ulong)split); U32(index, 72, 0); index[79] = 1;
        System.Text.Encoding.UTF8.GetBytes(ArchiveName).CopyTo(index, 80);
        File.WriteAllBytes(Path.Combine(folder, "bundles.nxa"), Dsar(index));
        File.WriteAllBytes(Path.Combine(folder, "bundle_database.data"), BundleDatabase([ArchiveName, ArchiveName + ".stream"]));
    }

    public static byte[] BundleDatabase(params string[][] groups)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(3u);
        writer.Write((uint)groups.Length);
        foreach (var group in groups)
        {
            writer.Write((uint)group.Length);
            foreach (var name in group)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(name);
                writer.Write((uint)bytes.Length);
                writer.Write(bytes);
            }
        }
        writer.Flush();
        return output.ToArray();
    }
}

internal sealed class UnitSource(params (ulong Id, byte[] Data)[] units) : Quartermaster.Repatcher.Archives.IUnitResourceSource
{
    private readonly Dictionary<ulong, byte[]> units = units.ToDictionary(u => u.Id, u => u.Data);
    public bool ContainsUnit(ulong unitId) => units.ContainsKey(unitId);
    public byte[] ReadUnit(ulong unitId, CancellationToken cancellationToken = default) => units[unitId].ToArray();
}

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quartermaster-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, true);
}
