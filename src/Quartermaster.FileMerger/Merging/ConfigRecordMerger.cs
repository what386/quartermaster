namespace Quartermaster.FileMerger;

/// <summary>Matches records by identity. An unchanged stock record never undoes another mod's deletion.</summary>
internal static class ConfigRecordMerger
{
    private sealed record Operation(FileMod Mod, byte[]? Bytes);

    internal static IReadOnlyList<ConfigRecord> Merge(ConfigRecordSet baseline,
        IReadOnlyList<(FileMod Mod, ConfigRecordSet Set)> inputs, ConfigValueMerger values, CancellationToken ct)
    {
        var stock = baseline.Records.ToDictionary(record => record.Id);
        var sources = inputs.Select(input => (input.Mod, Records: input.Set.Records.ToDictionary(record => record.Id))).ToArray();
        var order = baseline.Records.Select(record => record.Id).ToList();
        order.AddRange(sources.SelectMany(source => source.Records.Keys).Where(id => !stock.ContainsKey(id)).Distinct().Order());
        var output = new List<ConfigRecord>();
        foreach (var id in order)
        {
            ct.ThrowIfCancellationRequested();
            var recordId = $"{baseline.Name} {id:x16}";
            if (!stock.TryGetValue(id, out var original))
            {
                var additions = sources.Where(source => source.Records.ContainsKey(id)).ToArray();
                var winner = additions[^1]; var bytes = winner.Records[id].Bytes;
                foreach (var previous in additions.Take(additions.Length - 1))
                    if (!previous.Records[id].Bytes.AsSpan().SequenceEqual(bytes)) values.Conflict(previous.Mod, winner.Mod, -1, recordId);
                output.Add(new(id, bytes));
                continue;
            }
            var operations = new List<Operation>();
            foreach (var source in sources)
            {
                if (!source.Records.TryGetValue(id, out var record)) operations.Add(new(source.Mod, null));
                else if (!record.Bytes.AsSpan().SequenceEqual(original.Bytes)) operations.Add(new(source.Mod, record.Bytes));
            }
            if (operations.Count == 0) { output.Add(original); continue; }
            if (operations[^1].Bytes is null)
            {
                foreach (var previous in operations.Take(operations.Count - 1).Where(op => op.Bytes is not null))
                    values.Conflict(previous.Mod, operations[^1].Mod, -1, recordId);
                continue;
            }
            var deleted = operations.FindLastIndex(operation => operation.Bytes is null);
            var active = operations.Skip(deleted + 1).ToArray();
            var merged = values.Merge(original.Bytes, active.Select(op => new ConfigValueInput(op.Mod, op.Bytes!)).ToArray(), recordId);
            if (deleted >= 0)
            {
                var restoredBy = active[0].Mod;
                foreach (var previous in operations.Take(deleted + 1))
                {
                    if (previous.Bytes is null) { values.Conflict(previous.Mod, restoredBy, -1, recordId); continue; }
                    for (var offset = 0; offset < baseline.Stride; offset += 4)
                    {
                        var size = Math.Min(4, baseline.Stride - offset);
                        var value = previous.Bytes.AsSpan(offset, size);
                        if (!value.SequenceEqual(original.Bytes.AsSpan(offset, size)) && !value.SequenceEqual(merged.Bytes.AsSpan(offset, size)))
                            values.Conflict(previous.Mod, merged.Owners.GetValueOrDefault(offset, restoredBy), offset, recordId);
                    }
                }
            }
            output.Add(new(id, merged.Bytes));
        }
        return output;
    }
}
