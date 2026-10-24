using System.Buffers.Binary;

namespace Quartermaster.FileMerger;

internal sealed record ConfigBlock(int Header, uint Type, int Size)
{
    public int Data => Header + 24;
}

internal sealed record ConfigDocument(byte[] Bytes, IReadOnlyList<ConfigBlock> Blocks)
{
    internal static ConfigDocument Parse(byte[] bytes)
    {
        var blocks = new List<ConfigBlock>();
        var cursor = 0;
        while (cursor < bytes.Length)
        {
            var header = -1;
            for (var i = cursor; i <= Math.Min(bytes.Length - 24, cursor + 56); i++)
                if (bytes.AsSpan(i, 8).SequenceEqual("LDLD\x01\0\0\0"u8)) { header = i; break; }
            if (header < 0) throw new InvalidDataException($"Invalid LDLD config block near offset {cursor}.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 12));
            if (size > bytes.Length - header - 24) throw new InvalidDataException("LDLD block extends beyond the config file.");
            if (bytes[header + 16] != 1) throw new InvalidDataException("Unsupported LDLD block flag.");
            blocks.Add(new(header, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(header + 8)), (int)size));
            cursor = header + 24 + (int)size;
        }
        if (blocks.Count == 0) throw new InvalidDataException("Game config contains no LDLD blocks.");
        return new(bytes, blocks);
    }
}
