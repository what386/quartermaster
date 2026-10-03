namespace Quartermaster.Repatcher.Formats;

/// <summary>An 80-byte resource record. Unknown fields are preserved when serialized.</summary>
public sealed record ResourceRecord(
    ulong FileId, ulong TypeId, ulong DataOffset, ulong StreamOffset, ulong GpuOffset,
    ulong Unknown1, ulong Unknown2, uint DataSize, uint StreamSize, uint GpuSize,
    uint Unknown3, uint Unknown4, uint EntryIndex)
{
    internal static ResourceRecord Read(ReadOnlySpan<byte> data) => new(
        BinaryData.U64(data, 0), BinaryData.U64(data, 8), BinaryData.U64(data, 16),
        BinaryData.U64(data, 24), BinaryData.U64(data, 32), BinaryData.U64(data, 40),
        BinaryData.U64(data, 48), BinaryData.U32(data, 56), BinaryData.U32(data, 60),
        BinaryData.U32(data, 64), BinaryData.U32(data, 68), BinaryData.U32(data, 72), BinaryData.U32(data, 76));

    internal void Write(Span<byte> data)
    {
        BinaryData.Put64(data, 0, FileId); BinaryData.Put64(data, 8, TypeId);
        BinaryData.Put64(data, 16, DataOffset); BinaryData.Put64(data, 24, StreamOffset);
        BinaryData.Put64(data, 32, GpuOffset); BinaryData.Put64(data, 40, Unknown1);
        BinaryData.Put64(data, 48, Unknown2); BinaryData.Put32(data, 56, DataSize);
        BinaryData.Put32(data, 60, StreamSize); BinaryData.Put32(data, 64, GpuSize);
        BinaryData.Put32(data, 68, Unknown3); BinaryData.Put32(data, 72, Unknown4);
        BinaryData.Put32(data, 76, EntryIndex);
    }
}

public sealed record ResourceType(ulong TypeId, ulong Count);

public sealed class PatchTable
{
    public const uint Magic = 4026531857;
    public const ulong UnitTypeId = 16187218042980615487;
    public int ItemTableOffset { get; }
    public int DataStart { get; }
    public IReadOnlyList<ResourceType> Types { get; }
    public IReadOnlyList<ResourceRecord> Resources { get; }

    private PatchTable(int offset, int end, ResourceType[] types, ResourceRecord[] records)
        => (ItemTableOffset, DataStart, Types, Resources) = (offset, end, Array.AsReadOnly(types), Array.AsReadOnly(records));

    /// <summary>Reads a TOC; resource payloads may be stored outside the supplied bytes.</summary>
    public static PatchTable Read(ReadOnlySpan<byte> data)
    {
        BinaryData.Slice(data, 0, 72);
        if (BinaryData.U32(data, 0) != Magic) throw new InvalidDataException("Invalid patch/archive magic.");
        var typeCount = BinaryData.U32(data, 4);
        var fileCount = BinaryData.U32(data, 8);
        var itemStart = 72L + 32L * typeCount;
        var end = itemStart + 80L * fileCount;
        BinaryData.Slice(data, 0, end);
        var types = new ResourceType[(int)typeCount];
        ulong total = 0;
        for (var i = 0; i < types.Length; i++)
        {
            types[i] = new(BinaryData.U64(data, 72L + 32L * i + 8), BinaryData.U64(data, 72L + 32L * i + 16));
            if (types[i].Count > fileCount || total > fileCount - types[i].Count)
                throw new InvalidDataException("Invalid resource type counts.");
            total += types[i].Count;
        }
        if (total != fileCount || types.Select(t => t.TypeId).Distinct().Count() != types.Length)
            throw new InvalidDataException("Resource type table does not match the item count.");
        var records = new ResourceRecord[(int)fileCount];
        var counts = types.ToDictionary(t => t.TypeId, _ => 0UL);
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = ResourceRecord.Read(BinaryData.Slice(data, itemStart + i * 80L, 80));
            if (!counts.ContainsKey(records[i].TypeId)) throw new InvalidDataException("Unknown resource type in item table.");
            counts[records[i].TypeId]++;
        }
        if (types.Any(t => counts[t.TypeId] != t.Count)) throw new InvalidDataException("Resource type counts do not match records.");
        return new((int)itemStart, (int)end, types, records);
    }
}
