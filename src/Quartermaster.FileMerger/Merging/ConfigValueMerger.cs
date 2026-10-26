namespace Quartermaster.FileMerger;

internal sealed record ConfigInput(FileMod Mod, ConfigDocument Document);
internal sealed record ConfigValueInput(FileMod Mod, byte[] Bytes);
internal sealed record ConfigValues(byte[] Bytes, IReadOnlyDictionary<int, FileMod> Owners);

/// <summary>Whole-cell three-way changes and their final ownership, shared by raw blocks and parsed records.</summary>
internal sealed class ConfigValueMerger(string target, int blockIndex, uint blockType, List<MergeConflict> conflicts, CancellationToken ct)
{
    internal ConfigValues Merge(byte[] baseline, IReadOnlyList<ConfigValueInput> inputs, string? recordId = null)
    {
        var output = baseline.ToArray();
        var writers = new Dictionary<int, List<(FileMod Mod, byte[] Value)>>();
        foreach (var input in inputs)
        {
            if (input.Bytes.Length != baseline.Length) throw new InvalidDataException("Config values have incompatible lengths.");
            for (var offset = 0; offset < baseline.Length; offset += 4)
            {
                if ((offset & 0x3fff) == 0) ct.ThrowIfCancellationRequested();
                var size = Math.Min(4, baseline.Length - offset);
                var value = input.Bytes.AsSpan(offset, size);
                if (baseline.AsSpan(offset, size).SequenceEqual(value)) continue;
                value.CopyTo(output.AsSpan(offset));
                if (!writers.TryGetValue(offset, out var history)) writers[offset] = history = [];
                history.Add((input.Mod, value.ToArray()));
            }
        }
        var owners = new Dictionary<int, FileMod>();
        foreach (var (offset, history) in writers)
        {
            var winner = history[^1]; owners[offset] = winner.Mod;
            foreach (var prior in history.Take(history.Count - 1).Where(prior => !prior.Value.AsSpan().SequenceEqual(winner.Value)))
                Conflict(prior.Mod, winner.Mod, offset, recordId);
        }
        return new(output, owners);
    }

    internal void Conflict(FileMod loser, FileMod winner, int offset, string? recordId) =>
        conflicts.Add(new(target, blockType, blockIndex, offset, loser.Name, winner.Name)
        { LosingSource = loser.Source, WinningSource = winner.Source, RecordId = recordId });
}
