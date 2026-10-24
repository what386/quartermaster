namespace Quartermaster.FileMerger;

/// <summary>Merges changes relative to a verified stock config, in low-to-high priority order (last wins).</summary>
public static class ConfigMerger
{
    public static MergeResult Merge(string target, ReadOnlyMemory<byte> stock, IReadOnlyList<FileMod> orderedMods, CancellationToken ct = default)
    {
        target = FileModPaths.ValidateTarget(target);
        ct.ThrowIfCancellationRequested();
        var original = ConfigDocument.Parse(GameConfigCodec.Decrypt(stock.Span));
        var stockHash = ConfigHash.Compute(original.Bytes);
        var stockFingerprint = FileModPaths.Fingerprint(stock.Span);
        var inputs = new List<(FileMod Mod, ConfigDocument Document, bool Compatible)>();
        foreach (var mod in orderedMods)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(mod.Name)) throw new InvalidDataException("A file mod needs a name.");
                if (FileModPaths.ValidateTarget(mod.Target) != target) throw new InvalidDataException("Mod target does not match the merge target.");
                if (mod.StockFingerprints.Count > 0 && !mod.StockFingerprints.Contains(stockFingerprint, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Needs a new version: this mod does not recognize the installed stock game file.");
                var document = ConfigDocument.Parse(GameConfigCodec.Decrypt(mod.Content.Span));
                if (ConfigHash.Compute(document.Bytes) != stockHash)
                    throw new InvalidDataException("Needs a new version: this config's game hash differs from the stock file.");
                inputs.Add((mod, document, Compatible(original, document)));
            }
            catch (InvalidDataException ex)
            {
                var failure = new InvalidDataException($"File mod '{mod.Name}' ({mod.Source}, {target}): {ex.Message}", ex);
                failure.Data["Quartermaster.FileModSource"] = mod.Source;
                throw failure;
            }
        }
        if (inputs.Count == 0) return new(target, stock.ToArray(), [], []);
        if (inputs.Any(input => !input.Compatible))
        {
            var winner = inputs[^1].Mod;
            var skipped = inputs.Take(inputs.Count - 1).Select(input => new SkippedFileMod(target, input.Mod.Name,
                $"A config changes its layout or length; only the highest-priority mod '{winner.Name}' can be installed for this file.")
                { Source = input.Mod.Source, WinningMod = winner.Name, WinningSource = winner.Source }).ToArray();
            return new(target, winner.Content.ToArray(), [], skipped);
        }
        var output = original.Bytes.ToArray();
        var writers = new Dictionary<(int Block, int Offset), List<(FileMod Mod, byte[] Value)>>();
        var conflicts = new List<MergeConflict>();
        foreach (var input in inputs)
        {
            for (var index = 0; index < original.Blocks.Count; index++)
            {
                var baseline = original.Blocks[index]; var modified = input.Document.Blocks[index];
                for (var offset = 0; offset < baseline.Size; offset += 4)
                {
                    if ((offset & 0x3fff) == 0) ct.ThrowIfCancellationRequested();
                    // Comparing complete 32-bit cells prevents two partial float/int edits from becoming a third value.
                    var size = Math.Min(4, baseline.Size - offset);
                    var before = original.Bytes.AsSpan(baseline.Data + offset, size);
                    var value = input.Document.Bytes.AsSpan(modified.Data + offset, size);
                    if (before.SequenceEqual(value)) continue;
                    var current = output.AsSpan(baseline.Data + offset, size);
                    if (!writers.TryGetValue((index, offset), out var history)) writers[(index, offset)] = history = [];
                    history.Add((input.Mod, value.ToArray()));
                    value.CopyTo(current);
                }
            }
        }
        foreach (var (cell, history) in writers)
        {
            var winner = history[^1];
            foreach (var previous in history.Take(history.Count - 1).Where(previous => !previous.Value.AsSpan().SequenceEqual(winner.Value)))
                conflicts.Add(new(target, original.Blocks[cell.Block].Type, cell.Block, cell.Offset, previous.Mod.Name, winner.Mod.Name)
                { LosingSource = previous.Mod.Source, WinningSource = winner.Mod.Source });
        }
        if (output.AsSpan().SequenceEqual(original.Bytes)) return new(target, stock.ToArray(), conflicts.ToArray(), []);
        ct.ThrowIfCancellationRequested();
        var corrected = ConfigHash.Correct(original, output, stockHash);
        var content = GameConfigCodec.Encrypt(corrected);
        if (!GameConfigCodec.Decrypt(content).AsSpan().SequenceEqual(corrected)) throw new InvalidDataException("Merged config encryption did not verify.");
        return new(target, content, conflicts.ToArray(), []);
    }

    private static bool Compatible(ConfigDocument baseline, ConfigDocument modified)
    {
        if (baseline.Blocks.Count != modified.Blocks.Count) return false;
        var previousEnd = 0;
        for (var index = 0; index < baseline.Blocks.Count; index++)
        {
            var before = baseline.Blocks[index]; var after = modified.Blocks[index];
            if (before.Type != after.Type || before.Header != after.Header ||
                !baseline.Bytes.AsSpan(previousEnd, before.Header - previousEnd).SequenceEqual(modified.Bytes.AsSpan(previousEnd, after.Header - previousEnd))) return false;
            if (before.Size != after.Size)
            {
                // Supported file mods append 8..15 bytes of hash correction to the last payload.
                // Ignore that trailer when diffing, then generate a fresh one for the combined result.
                var extra = after.Size - before.Size;
                if (index != baseline.Blocks.Count - 1 || extra < 8 || extra > 15 ||
                    modified.Bytes.AsSpan(after.Data + before.Size, extra - 8).IndexOfAnyExcept((byte)0) >= 0) return false;
            }
            previousEnd = before.Data + before.Size;
        }
        return true;
    }
}
