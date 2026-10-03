using System.Buffers.Binary;

namespace Quartermaster.Repatcher.Formats;

internal static class BinaryData
{
    public static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, long offset, long size)
    {
        if (offset < 0 || size < 0 || offset > data.Length || size > data.Length - offset)
            throw new InvalidDataException("Binary range extends beyond the available data.");
        return data.Slice((int)offset, (int)size);
    }

    public static uint U32(ReadOnlySpan<byte> data, long offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(Slice(data, offset, 4));
    public static ulong U64(ReadOnlySpan<byte> data, long offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(Slice(data, offset, 8));
    public static void Put32(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(offset, 4), value);
    public static void Put64(Span<byte> data, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(offset, 8), value);
    public static int Length(ulong value) => value <= int.MaxValue
        ? (int)value : throw new InvalidDataException("Resource exceeds the supported in-memory size.");
    public static byte[] ReadAt(Stream stream, ulong offset, int size)
    {
        if (offset > (ulong)stream.Length || size < 0 || (ulong)size > (ulong)stream.Length - offset)
            throw new InvalidDataException("Archive range extends beyond the file.");
        var data = new byte[size];
        stream.Position = (long)offset;
        stream.ReadExactly(data);
        return data;
    }
}
