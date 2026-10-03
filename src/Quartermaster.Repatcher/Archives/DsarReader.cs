using K4os.Compression.LZ4;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Archives;

internal sealed class DsarReader
{
    public const uint Magic = 1380012868;
    private sealed record Chunk(ulong Offset, ulong CompressedOffset, int Size, int CompressedSize, byte Compression);
    private readonly string path;
    private readonly Chunk[] chunks;

    public DsarReader(string path)
    {
        this.path = path;
        using var file = File.OpenRead(path);
        var header = BinaryData.ReadAt(file, 0, 32);
        if (BinaryData.U32(header, 0) != Magic) throw new InvalidDataException("Invalid DSAR magic.");
        var count = BinaryData.Length(BinaryData.U32(header, 8));
        var table = BinaryData.ReadAt(file, 32, BinaryData.Length((ulong)count * 32));
        chunks = new Chunk[count];
        ulong previousEnd = 0;
        for (var i = 0; i < count; i++)
        {
            var row = table.AsSpan(i * 32, 32);
            var chunk = new Chunk(BinaryData.U64(row, 0), BinaryData.U64(row, 8),
                BinaryData.Length(BinaryData.U32(row, 16)), BinaryData.Length(BinaryData.U32(row, 20)), row[24]);
            if (chunk.Offset != previousEnd || chunk.Size == 0 || chunk.CompressedOffset < (ulong)(32 + table.Length) ||
                chunk.CompressedOffset > (ulong)file.Length || (ulong)chunk.CompressedSize > (ulong)file.Length - chunk.CompressedOffset)
                throw new InvalidDataException("Invalid DSAR chunk range.");
            if (chunk.Compression is not (0 or 3) || (chunk.Compression == 0 && chunk.Size != chunk.CompressedSize))
                throw new InvalidDataException("Unsupported DSAR compression or invalid chunk size.");
            previousEnd = checked(chunk.Offset + (ulong)chunk.Size);
            chunks[i] = chunk;
        }
    }

    public ulong Length => chunks.Length == 0 ? 0 : chunks[^1].Offset + (ulong)chunks[^1].Size;

    public byte[] Read(ulong offset, int size, CancellationToken cancellationToken = default)
    {
        if (offset > Length || size < 0 || (ulong)size > Length - offset)
            throw new InvalidDataException("DSAR resource extends beyond the archive.");
        var result = new byte[size];
        using var file = File.OpenRead(path);
        var written = 0;
        foreach (var chunk in chunks)
        {
            if (written == size) break;
            if (chunk.Offset + (ulong)chunk.Size <= offset || chunk.Offset >= offset + (ulong)size) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var compressed = BinaryData.ReadAt(file, chunk.CompressedOffset, chunk.CompressedSize);
            byte[] decoded;
            if (chunk.Compression == 0) decoded = compressed;
            else
            {
                decoded = new byte[chunk.Size];
                if (LZ4Codec.Decode(compressed, 0, compressed.Length, decoded, 0, decoded.Length) != decoded.Length)
                    throw new InvalidDataException("Invalid LZ4 block.");
            }
            var start = (int)(Math.Max(offset, chunk.Offset) - chunk.Offset);
            var length = Math.Min(chunk.Size - start, size - written);
            decoded.AsSpan(start, length).CopyTo(result.AsSpan(written));
            written += length;
        }
        if (written != size) throw new InvalidDataException("Incomplete DSAR resource.");
        return result;
    }
}
