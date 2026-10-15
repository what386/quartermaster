using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Repair;

internal static class LegacyRecovery
{
    // Unlike the reference's lenient repair, never truncate payloads or guess an
    // ambiguous body from row order. Valid offsets remain authoritative.
    public static byte[] Recover(ReadOnlyMemory<byte> input, IList<RepairWarning> warnings)
    {
        var table = PatchTable.Read(input.Span);
        var invalid = table.Resources.Select((r, i) => (Record: r, Index: i)).Where(item =>
            item.Record.DataSize > 0 && (item.Record.DataOffset < (ulong)table.DataStart ||
            item.Record.DataOffset > (ulong)input.Length || item.Record.DataSize > (ulong)input.Length - item.Record.DataOffset)).ToArray();
        if (invalid.Length == 0) return input.ToArray();
        var badRows = invalid.Select(r => r.Index).ToHashSet();
        var valid = table.Resources.Select((r, i) => (r, i)).Where(item => !badRows.Contains(item.i) && item.r.DataSize > 0)
            .Select(item => item.r).OrderBy(r => r.DataOffset).ToArray();
        var gaps = new List<(ulong Start, ulong Size)>();
        var end = (ulong)table.DataStart;
        foreach (var r in valid)
        {
            if (r.DataOffset < end) throw new InvalidDataException("Overlapping legacy bodies cannot be recovered safely.");
            if (r.DataOffset > end) gaps.Add((end, r.DataOffset - end));
            end = checked(r.DataOffset + r.DataSize);
        }
        if (end < (ulong)input.Length) gaps.Add((end, (ulong)input.Length - end));
        // Resolve all rows before writing; equal-sized candidate gaps are ambiguous.
        var assignments = invalid.Select(item => (item, Candidates: gaps.Where(g => g.Size == item.Record.DataSize).ToArray())).ToArray();
        if (assignments.Any(a => a.Candidates.Length != 1) ||
            assignments.Select(a => a.Candidates[0].Start).Distinct().Count() != assignments.Length)
            throw new InvalidDataException("Legacy payload locations are ambiguous or incomplete; refusing inferred truncation or row-order recovery.");
        var output = input.ToArray();
        foreach (var (item, candidates) in assignments)
        {
            BinaryData.Put64(output, table.ItemTableOffset + item.Index * 80 + 16, candidates[0].Start);
            warnings.Add(new("LegacyOffsetRecovered", $"Recovered resource {item.Record.FileId:x16} at 0x{candidates[0].Start:x} from its unique complete payload gap."));
        }
        return output;
    }
}
