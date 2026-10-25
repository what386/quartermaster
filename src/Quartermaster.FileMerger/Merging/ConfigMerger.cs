namespace Quartermaster.FileMerger;

/// <summary>Merges changes relative to a verified stock config, in low-to-high priority order (last wins).</summary>
public static class ConfigMerger
{
    public static MergeResult Merge(string target, ReadOnlyMemory<byte> stock, IReadOnlyList<FileMod> orderedMods, CancellationToken ct = default, ConfigTableRegistry? registry = null)
    {
        registry ??= ConfigTableRegistry.Default;
        target = FileModPaths.ValidateTarget(target);
        ct.ThrowIfCancellationRequested();
        var original = ConfigDocument.Parse(GameConfigCodec.Decrypt(stock.Span));
        var stockHash = ConfigHash.Compute(original.Bytes);
        var stockFingerprint = FileModPaths.Fingerprint(stock.Span);
        var inputs = new List<ConfigInput>();
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
                inputs.Add(new(mod, document));
            }
            catch (InvalidDataException ex)
            {
                var failure = new InvalidDataException($"File mod '{mod.Name}' ({mod.Source}, {target}): {ex.Message}", ex);
                failure.Data["Quartermaster.FileModSource"] = mod.Source;
                throw failure;
            }
        }
        if (inputs.Count == 0) return new(target, stock.ToArray(), [], []);
        if (!StructuredConfigMerger.TryMerge(target, original, inputs, registry, ct, out var output, out var conflicts, out var reason))
        {
            var winner = inputs[^1].Mod;
            var skipped = inputs.Take(inputs.Count - 1).Select(input => new SkippedFileMod(target, input.Mod.Name,
                $"{reason} Only the highest-priority mod '{winner.Name}' can be installed for this file.")
            { Source = input.Mod.Source, WinningMod = winner.Name, WinningSource = winner.Source }).ToArray();
            return new(target, winner.Content.ToArray(), [], skipped);
        }
        if (output.AsSpan().SequenceEqual(original.Bytes)) return new(target, stock.ToArray(), conflicts, []);
        ct.ThrowIfCancellationRequested();
        var rebuilt = ConfigDocument.Parse(output);
        var corrected = ConfigHash.Correct(rebuilt, output, stockHash);
        StructuredConfigMerger.Validate(ConfigDocument.Parse(corrected), original, registry, ct);
        var content = GameConfigCodec.Encrypt(corrected);
        if (!GameConfigCodec.Decrypt(content).AsSpan().SequenceEqual(corrected)) throw new InvalidDataException("Merged config encryption did not verify.");
        return new(target, content, conflicts, []);
    }
}
