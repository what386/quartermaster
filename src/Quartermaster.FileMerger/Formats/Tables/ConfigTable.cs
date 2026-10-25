using System.Buffers.Binary;

namespace Quartermaster.FileMerger;

internal sealed record ConfigRecord(ulong Id, byte[] Bytes);
internal sealed record ConfigRecordSet(string Name, int Stride, IReadOnlyList<ConfigRecord> Records);

/// <summary>Layouts with stable record identities and no unhandled payload-relative pointers.
/// Format facts are independently implemented from the SHODAN format research; no Lua is executed.</summary>
internal sealed record ConfigTable(ConfigTableDefinition Definition, int IdOffset, byte[] Header, IReadOnlyList<ConfigRecordSet> Sets, int SemanticLength)
{
    internal ConfigTableKind Kind => Definition.Kind;

    internal static ConfigTable? Read(ConfigDocument document, int index, ConfigTableRegistry registry, CancellationToken ct, bool allowTrailer = true)
    {
        var block = document.Blocks[index];
        if (!registry.TryGet(block.Type, out var definition)) return null;
        var payload = document.Bytes.AsSpan(block.Data, block.Size);
        var last = allowTrailer && index == document.Blocks.Count - 1;
        if (definition.Kind == ConfigTableKind.Passive) return ReadPassive(payload, definition, block.Data, last);
        ConfigTable? found = null;
        foreach (var variant in definition.Variants)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = definition.Kind == ConfigTableKind.Rows
                ? ReadRows(payload, definition, variant, block.Data, last, ct)
                : ReadKeyed(payload, definition, variant.RecordSize, block.Data, last, ct);
            if (candidate is null) continue;
            if (found is not null) return null;
            found = candidate;
        }
        return found;
    }

    private static ConfigTable? ReadRows(ReadOnlySpan<byte> data, ConfigTableDefinition definition, ConfigTableVariant variant, int start, bool last, CancellationToken ct)
    {
        var stride = variant.RecordSize;
        if (data.Length < 16 || U32(data, 12) != 0) return null;
        var count = U32(data, 8);
        if (count > definition.MaxRecords || 16L + (long)count * stride > data.Length) return null;
        var length = checked(16 + (int)count * stride);
        if (!ValidEnd(data, length, start, last)) return null;
        var records = Records(data, 16, (int)count, stride, ct, variant.IdOffset);
        if (records is null) return null;
        var header = data[..16].ToArray(); header.AsSpan(8, 4).Clear();
        return new(definition, variant.IdOffset, header, [new("record", stride, records)], length);
    }

    private static ConfigTable? ReadPassive(ReadOnlySpan<byte> data, ConfigTableDefinition definition, int start, bool last)
    {
        if (data.Length < 56 || U32(data, 28) != 0 || U32(data, 44) != 0) return null;
        var modifiers = U32(data, 24); var stats = U32(data, 40);
        if (modifiers > definition.MaxRecords || stats > definition.MaxRecords) return null;
        var statsAt = 56 + (int)modifiers * 16;
        var length = statsAt + (int)stats * 12;
        if (length > data.Length || !ValidEnd(data, length, start, last) ||
            !ValidPointer(U64(data, 16), modifiers, 56) || !ValidPointer(U64(data, 32), stats, statsAt)) return null;
        var mods = Records(data, 56, (int)modifiers, 16);
        var weapons = Records(data, statsAt, (int)stats, 12);
        if (mods is null || weapons is null) return null;
        var header = data[..56].ToArray(); header.AsSpan(16, 32).Clear();
        return new(definition, 0, header, [new("modifier", 16, mods), new("weapon stat", 12, weapons)], length);
    }

    private static bool ValidPointer(ulong pointer, uint count, int expected) =>
        count == 0 ? pointer is 0 or ulong.MaxValue || pointer == (ulong)expected : pointer == (ulong)expected;

    private static IReadOnlyList<ConfigRecord>? Records(ReadOnlySpan<byte> data, int at, int count, int stride, CancellationToken ct = default, int idOffset = 0)
    {
        var records = new List<ConfigRecord>(count); var ids = new HashSet<ulong>();
        for (var i = 0; i < count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var record = data.Slice(at + i * stride, stride); var id = U32(record, idOffset);
            if (!ids.Add(id)) return null;
            records.Add(new(id, record.ToArray()));
        }
        return records;
    }

    private static ConfigTable? ReadKeyed(ReadOnlySpan<byte> data, ConfigTableDefinition definition, int stride, int start, bool last, CancellationToken ct)
    {
        var keys = new List<(ulong Id, uint Row)>();
        var ids = new HashSet<ulong>(); var rows = new HashSet<uint>();
        ConfigTable? found = null; var maxRow = 0u; var probes = 0;
        for (var buckets = 1; buckets <= Math.Min(data.Length / 16, definition.MaxRecords * 2); buckets++)
        {
            if ((buckets & 1023) == 0) ct.ThrowIfCancellationRequested();
            var at = (buckets - 1) * 16;
            var key = U64(data, at); var row = U32(data, at + 8);
            if (U32(data, at + 12) != 0 || row >= definition.MaxRecords) break;
            if (key == 0)
            {
                if (row != 0) break;
            }
            else
            {
                if (!ids.Add(key) || !rows.Add(row)) break;
                keys.Add((key, row)); maxRow = Math.Max(maxRow, row);
            }
            var length = (long)buckets * 16 + (long)keys.Count * stride;
            if (length > data.Length || (keys.Count > 0 ? maxRow >= keys.Count : buckets != 2) ||
                !ValidEnd(data, (int)length, start, last)) continue;
            var valid = true;
            for (var slot = 0; slot < buckets && valid; slot++)
            {
                var id = U64(data, slot * 16);
                if (id == 0) continue;
                var probe = (int)(id % (ulong)buckets);
                while (probe != slot)
                {
                    if (++probes > 4_000_000 || U64(data, probe * 16) == 0) { valid = false; break; }
                    probe = (probe + 1) % buckets;
                }
            }
            if (!valid) continue;
            if (found is not null) return null; // More than one index boundary fits; do not guess.
            var records = new List<ConfigRecord>(keys.Count);
            foreach (var item in keys.OrderBy(item => item.Row))
                records.Add(new(item.Id, data.Slice(buckets * 16 + (int)item.Row * stride, stride).ToArray()));
            found = new(definition, 0, [], [new("entity", stride, records)], (int)length);
        }
        return found;
    }

    // A recognized trailer is padding to an absolute 8-byte boundary followed by the correction word.
    // Nothing else after a table is discarded as padding or inferred from byte anchors.
    private static bool ValidEnd(ReadOnlySpan<byte> data, int end, int start, bool last)
    {
        if (end == data.Length) return true;
        var padding = (8 - ((start + end) & 7)) & 7;
        return last && data.Length - end == padding + 8 && data.Slice(end, padding).IndexOfAnyExcept((byte)0) < 0;
    }

    internal byte[] Write(IReadOnlyList<IReadOnlyList<ConfigRecord>> sets, byte[] header, CancellationToken ct)
    {
        if (sets.Any(records => records.Count > Definition.MaxRecords)) throw new InvalidDataException("Merged config table exceeds the record limit.");
        if (Kind == ConfigTableKind.Keyed)
        {
            var records = sets[0]; var buckets = checked(Math.Max(1, records.Count) * 2);
            var output = new byte[checked(buckets * 16 + records.Count * Sets[0].Stride)];
            var probes = 0;
            for (var row = 0; row < records.Count; row++)
            {
                ct.ThrowIfCancellationRequested(); var record = records[row]; var slot = (int)(record.Id % (ulong)buckets);
                while (U64(output, slot * 16) != 0)
                {
                    if (++probes > 4_000_000) throw new InvalidDataException("Merged config hash index has too many collisions.");
                    slot = (slot + 1) % buckets;
                }
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(slot * 16), record.Id);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(slot * 16 + 8), (uint)row);
                record.Bytes.CopyTo(output, buckets * 16 + row * Sets[0].Stride);
            }
            return output;
        }
        if (Kind == ConfigTableKind.Passive && sets.Any(records => records.Count > 4096))
            throw new InvalidDataException("Merged armor passive exceeds the supported array-count limit.");
        if (Kind == ConfigTableKind.Passive && sets[0].Count(record => U32(record.Bytes, 12) != 0) > 3)
            throw new InvalidDataException("Merged armor passive would exceed the game's limit of three described modifiers.");
        var length = checked(header.Length + sets.Select((records, i) => checked(records.Count * Sets[i].Stride)).Sum());
        var bytes = new byte[length]; header.CopyTo(bytes, 0);
        if (Kind == ConfigTableKind.Rows) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)sets[0].Count);
        else
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), sets[0].Count == 0 ? ulong.MaxValue : 56);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), (uint)sets[0].Count);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), sets[1].Count == 0 ? ulong.MaxValue : (ulong)(56 + sets[0].Count * 16));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), (uint)sets[1].Count);
        }
        var cursor = header.Length;
        for (var i = 0; i < sets.Count; i++)
            foreach (var record in sets[i]) { record.Bytes.CopyTo(bytes, cursor); cursor += record.Bytes.Length; }
        return bytes;
    }

    internal static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    internal static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
}
