using System.Buffers.Binary;

namespace Quartermaster.FileMerger;

internal static class StructuredConfigMerger
{
    internal static bool TryMerge(string target, ConfigDocument stock, IReadOnlyList<ConfigInput> inputs,
        ConfigTableRegistry registry, CancellationToken ct, out byte[] output, out IReadOnlyList<MergeConflict> conflicts, out string reason)
    {
        output = []; conflicts = []; reason = "The config block layout cannot be matched safely.";
        if (inputs.Any(input => !SameFrames(stock, input.Document))) return false;
        if (!SameRecordGroups(stock, inputs, registry, ct))
        { reason = "Records moved between repeated config blocks; this block reordering cannot be merged safely."; return false; }
        var payloads = new List<byte[]>(); var reports = new List<MergeConflict>(); var changed = false;
        for (var index = 0; index < stock.Blocks.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var block = stock.Blocks[index];
            var values = new ConfigValueMerger(target, index, block.Type, reports, ct);
            var table = ConfigTable.Read(stock, index, registry, ct, allowTrailer: false);
            if (table is not null)
            {
                var tables = inputs.Select(input => ConfigTable.Read(input.Document, index, registry, ct, baseline: table)).ToArray();
                if (tables.Any(candidate => candidate is null || !SameTable(table, candidate)))
                { reason = "A config table has an unsupported or ambiguous record layout."; return false; }
                var header = values.Merge(table.Header, inputs.Select((input, i) => new ConfigValueInput(input.Mod, tables[i]!.Header)).ToArray(), "table header").Bytes;
                var sets = table.Sets.Select((set, setIndex) => ConfigRecordMerger.Merge(set,
                    inputs.Select((input, i) => (input.Mod, tables[i]!.Sets[setIndex])).ToArray(), values, ct)).ToArray();
                var edited = !header.AsSpan().SequenceEqual(table.Header) || sets.Where((records, i) => !SameRecords(table.Sets[i].Records, records)).Any();
                if (edited)
                {
                    try { payloads.Add(table.Write(sets, header, ct)); }
                    catch (InvalidDataException ex) { reason = ex.Message; return false; }
                    changed = true;
                }
                else payloads.Add(stock.Bytes.AsSpan(block.Data, table.SemanticLength).ToArray());
            }
            else
            {
                var rawInputs = new List<ConfigValueInput>();
                foreach (var input in inputs)
                {
                    var other = input.Document.Blocks[index];
                    if (!SameRawLength(block, other, input.Document, index))
                    { reason = "A resized config table has an unsupported or ambiguous structure."; return false; }
                    rawInputs.Add(new(input.Mod, input.Document.Bytes.AsSpan(other.Data, block.Size).ToArray()));
                }
                var baseline = stock.Bytes.AsSpan(block.Data, block.Size).ToArray();
                var merged = values.Merge(baseline, rawInputs).Bytes;
                payloads.Add(merged); changed |= !merged.AsSpan().SequenceEqual(baseline);
            }
        }
        conflicts = reports;
        if (!changed) { output = stock.Bytes; return true; }
        var length = stock.Blocks.Select((block, i) => (long)(i == 0 ? block.Header : block.Header - stock.Blocks[i - 1].Data - stock.Blocks[i - 1].Size) + 24 + payloads[i].Length).Sum();
        if (length > 256L * 1024 * 1024 - GameConfigCodec.SealBytes - 15)
        { reason = "The combined config exceeds the supported file-size limit."; return false; }
        using var stream = new MemoryStream((int)length);
        var cursor = 0;
        for (var index = 0; index < stock.Blocks.Count; index++)
        {
            var block = stock.Blocks[index];
            stream.Write(stock.Bytes.AsSpan(cursor, block.Header - cursor));
            var header = stock.Bytes.AsSpan(block.Header, 24).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)payloads[index].Length);
            stream.Write(header); stream.Write(payloads[index]); cursor = block.Data + block.Size;
        }
        output = stream.ToArray();
        Validate(ConfigDocument.Parse(output), stock, registry, ct);
        return true;
    }

    private static bool SameRecordGroups(ConfigDocument stock, IReadOnlyList<ConfigInput> inputs, ConfigTableRegistry registry, CancellationToken ct)
    {
        foreach (var group in stock.Blocks.Select((block, index) => (block, index))
            .Where(item => registry.TryGet(item.block.Type, out var definition) && definition.Kind != ConfigTableKind.Passive)
            .GroupBy(item => item.block.Type).Where(group => group.Count() > 1))
        {
            var owners = new Dictionary<(string Set, ulong Id), HashSet<int>>();
            foreach (var (_, index) in group)
                if (ConfigTable.Read(stock, index, registry, ct, allowTrailer: false) is { } table)
                    foreach (var set in table.Sets)
                        foreach (var record in set.Records)
                        {
                            if (!owners.TryGetValue((set.Name, record.Id), out var locations)) owners[(set.Name, record.Id)] = locations = [];
                            locations.Add(index);
                        }
            foreach (var input in inputs)
                foreach (var (_, index) in group)
                    if (ConfigTable.Read(input.Document, index, registry, ct, baseline: ConfigTable.Read(stock, index, registry, ct, allowTrailer: false)) is { } table)
                        foreach (var set in table.Sets)
                            foreach (var record in set.Records)
                                if (owners.TryGetValue((set.Name, record.Id), out var locations) && !locations.Contains(index)) return false;
        }
        return true;
    }

    private static bool SameTable(ConfigTable before, ConfigTable after) => before.Kind == after.Kind && before.IdOffset == after.IdOffset &&
        before.Sets.Count == after.Sets.Count && before.Sets.Select(set => set.Stride).SequenceEqual(after.Sets.Select(set => set.Stride)) &&
        (before.Kind != ConfigTableKind.Rows || before.Header.AsSpan(0, 8).SequenceEqual(after.Header.AsSpan(0, 8))) &&
        (before.Kind != ConfigTableKind.Passive || ConfigTable.U32(before.Header, 0) == ConfigTable.U32(after.Header, 0));

    private static bool SameRecords(IReadOnlyList<ConfigRecord> before, IReadOnlyList<ConfigRecord> after) =>
        before.Count == after.Count && before.Select((record, i) => record.Id == after[i].Id && record.Bytes.AsSpan().SequenceEqual(after[i].Bytes)).All(equal => equal);

    private static bool SameFrames(ConfigDocument stock, ConfigDocument other)
    {
        if (stock.Blocks.Count != other.Blocks.Count) return false;
        var a = 0; var b = 0;
        for (var index = 0; index < stock.Blocks.Count; index++)
        {
            var first = stock.Blocks[index]; var second = other.Blocks[index];
            if (first.Type != second.Type || first.Header - a != second.Header - b ||
                !stock.Bytes.AsSpan(a, first.Header - a).SequenceEqual(other.Bytes.AsSpan(b, second.Header - b))) return false;
            a = first.Data + first.Size; b = second.Data + second.Size;
        }
        return true;
    }

    private static bool SameRawLength(ConfigBlock stock, ConfigBlock mod, ConfigDocument document, int index)
    {
        if (stock.Size == mod.Size) return true;
        var padding = (8 - ((mod.Data + stock.Size) & 7)) & 7;
        return index == document.Blocks.Count - 1 && mod.Size - stock.Size == padding + 8 &&
            document.Bytes.AsSpan(mod.Data + stock.Size, padding).IndexOfAnyExcept((byte)0) < 0;
    }

    // Verify rebuilt table indexes, counts and offsets before sealing, and again after hash correction.
    internal static void Validate(ConfigDocument document, ConfigDocument stock, ConfigTableRegistry registry, CancellationToken ct)
    {
        for (var index = 0; index < stock.Blocks.Count; index++)
            if (ConfigTable.Read(stock, index, registry, ct, allowTrailer: false) is { } baseline)
            {
                var parsed = ConfigTable.Read(document, index, registry, ct, baseline: baseline);
                if (parsed is null || !SameTable(baseline, parsed))
                    throw new InvalidDataException("Merged config table failed its record/index/offset validation.");
            }
    }
}
