using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quartermaster.Core.Patching;
using Quartermaster.FileMerger;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library.Profiles;

public sealed record PreparedProfileFileMods(FileMergePlan Plan, string? Signature, IReadOnlyList<Guid> AppliedModIds, IReadOnlyList<string> Warnings)
{
    public Dictionary<Guid, string[]> ModWarnings { get; init; } = [];
}
public sealed record ProfileFileModHealth(bool Complete, string? Signature, IReadOnlyList<Guid> AppliedModIds, IReadOnlyList<string> Warnings)
{
    public static ProfileFileModHealth Empty => new(true, null, [], []);
    public IReadOnlyDictionary<Guid, string[]> ModWarnings { get; init; } = new Dictionary<Guid, string[]>();
}

/// <summary>Maps selected library variants to config merges in the same low-to-high order as archive patches.</summary>
public sealed class ProfileFileMods(ModContentStore contents)
{
    // Reuse decoded frames across option/order previews. Bytes are still hash-verified on every read.
    // Keep at most eight small payloads (32 MiB total); large packages are not retained.
    private readonly Dictionary<(string Path, string Hash), FileModDetection> detectionCache = [];
    private FileModDetection DetectVerifiedFile(PatchFile file, byte[] bytes, CancellationToken ct)
    {
        var key = (file.RelativePath, file.Sha256);
        lock (detectionCache)
        {
            if (detectionCache.TryGetValue(key, out var cached)) return cached;
        }
        var detected = new FileModDetector().DetectFile(file.RelativePath, bytes, ct);
        if (detected.Mods.Sum(mod => (long)mod.Content.Length) <= 4 * 1024 * 1024)
            lock (detectionCache)
            {
                if (detectionCache.Count >= 8) detectionCache.Remove(detectionCache.Keys.First());
                detectionCache[key] = detected;
            }
        return detected;
    }
    private readonly FileModInstaller installer = new(Path.Combine(contents.ApplicationDirectory, "filemods"));
    private sealed record Receipt(string GameDirectory, bool Complete, string? Signature, Guid[] AppliedModIds, string[] Warnings)
    {
        public Dictionary<Guid, string[]> ModWarnings { get; init; } = [];
    }
    private static string GameRoot(string dataDirectory) => Path.GetDirectoryName(ManagedPaths.CanonicalDirectory(dataDirectory))!;
    private string ReceiptPath(string dataDirectory)
    {
        var path = GameRoot(dataDirectory);
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        return ManagedPaths.Resolve(contents.ApplicationDirectory, "filemod-receipts/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".json");
    }

    public static string? Signature(LibraryState state, Profile profile)
    {
        var mods = state.Mods.ToDictionary(mod => mod.Id);
        var selected = ProfileEditor.InDeploymentOrder(profile).Where(entry => entry.Enabled).SelectMany(entry =>
            PatchSelection.Select(mods[entry.ModId], entry).Where(set => set.ContentKind == ModContentKind.FileMod)
                .Select(set => new { Mod = entry.ModId, Set = set.Id, set.FileModPayloadIndex,
                    Files = set.Files.Select(file => new { file.RelativePath, file.Sha256 }), Options = PatchSelection.OptionsHash(mods[entry.ModId], entry) })).ToArray();
        return selected.Length == 0 ? null : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(selected)));
    }

    public async Task<PreparedProfileFileMods> PrepareAsync(LibraryState state, Profile profile, string dataDirectory, CancellationToken ct)
    {
        var mods = state.Mods.ToDictionary(mod => mod.Id);
        var selected = new List<FileMod>(); var owners = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var entry in ProfileEditor.InDeploymentOrder(profile).Where(entry => entry.Enabled))
        {
            var mod = mods[entry.ModId];
            foreach (var set in PatchSelection.Select(mod, entry).Where(set => set.ContentKind == ModContentKind.FileMod))
            {
                try
                {
                    var file = set.Files.Single(file => file.Kind == PatchFileKind.Main);
                    var bytes = await contents.ReadVerifiedAsync(mod.Id, file, ct).ConfigureAwait(false);
                    var detected = DetectVerifiedFile(file, bytes, ct);
                    var payloads = set.FileModPayloadIndex is { } index
                        ? index < detected.Mods.Count ? new[] { detected.Mods[index] } : throw new InvalidDataException("File-mod variant no longer exists in its source.")
                        : detected.Mods;
                    if (payloads.Count == 0) throw new InvalidDataException("A stored file-mod payload could not be detected.");
                    foreach (var payload in payloads)
                    {
                        var source = $"{mod.Id:N}/{set.Id:N}/{payload.Source}";
                        var name = payload.Name == mod.Name ? mod.Name : $"{mod.Name} — {payload.Name}";
                        selected.Add(payload with { Name = name, Source = source }); owners[source] = mod.Id;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    var failure = new InvalidDataException($"Could not prepare file mod '{mod.Name}': {ex.Message}", ex);
                    failure.Data["Quartermaster.ModId"] = mod.Id;
                    throw failure;
                }
            }
        }
        FileMergePlan plan;
        try { plan = await installer.PrepareAsync(GameRoot(dataDirectory), selected, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex.Data["Quartermaster.FileModSource"] is string source && owners.ContainsKey(source))
        {
            ex.Data["Quartermaster.ModId"] = owners[(string)ex.Data["Quartermaster.FileModSource"]!];
            throw;
        }
        var skipped = plan.Skipped.Select(skip => skip.Source).ToHashSet(StringComparer.Ordinal);
        var applied = selected.Where(mod => !skipped.Contains(mod.Source)).Select(mod => owners[mod.Source]).Distinct().ToArray();
        var messages = new List<string>();
        var modWarnings = new Dictionary<Guid, List<string>>();
        void Add(string? source, string message)
        {
            if (source is null || !owners.TryGetValue(source, out var id)) return;
            if (!modWarnings.TryGetValue(id, out var issues)) modWarnings[id] = issues = [];
            issues.Add(message);
        }
        static string Names(IEnumerable<string> names) => string.Join("\n", names.Distinct(StringComparer.Ordinal).Select(name => "  " + name));
        foreach (var group in plan.Conflicts.GroupBy(conflict => (conflict.Target, conflict.WinningSource)))
        {
            var conflict = group.First();
            var message = $"Config file:\n  {conflict.Target}\n\nApplied mod:\n  {conflict.WinningMod}\n\nOverridden mods:\n{Names(group.Select(item => item.LosingMod))}\n\nThese mods edit the same parts of this config. The applied mod replaces overlapping edits from the overridden mods. Other edits are kept.\n\nDisable a conflicting mod or choose different options to avoid losing edits.";
            messages.Add(message);
            Add(conflict.WinningSource, message);
            foreach (var source in group.Select(item => item.LosingSource).Distinct()) Add(source, message);
        }
        foreach (var group in plan.Skipped.GroupBy(skip => (skip.Target, skip.WinningSource)))
        {
            var skip = group.First();
            var message = $"Config file:\n  {skip.Target}\n\nApplied mod:\n  {skip.WinningMod}\n\nSkipped mods:\n{Names(group.Select(item => item.Name))}\n\nThese file layouts cannot be merged. Only the applied mod is used for this entire file; all edits from the skipped mods are lost.\n\nDisable a conflicting mod or choose compatible options.";
            messages.Add(message);
            Add(skip.WinningSource, message);
            foreach (var source in group.Select(item => item.Source).Distinct()) Add(source, message);
        }
        var warnings = messages.ToArray();
        return new(plan, Signature(state, profile), applied, warnings)
        { ModWarnings = modWarnings.ToDictionary(pair => pair.Key, pair => pair.Value.Distinct().ToArray()) };
    }

    public Task BeginAsync(PreparedProfileFileMods prepared, string dataDirectory, CancellationToken ct) =>
        JsonFiles.WriteAsync(ReceiptPath(dataDirectory), new Receipt(GameRoot(dataDirectory), false, prepared.Signature, prepared.AppliedModIds.ToArray(), prepared.Warnings.ToArray()) { ModWarnings = prepared.ModWarnings }, ct);

    public async Task ApplyAsync(PreparedProfileFileMods prepared, string dataDirectory, CancellationToken ct)
    {
        await installer.ApplyAsync(prepared.Plan, ct).ConfigureAwait(false);
        await JsonFiles.WriteAsync(ReceiptPath(dataDirectory), new Receipt(GameRoot(dataDirectory), true, prepared.Signature, prepared.AppliedModIds.ToArray(), prepared.Warnings.ToArray()) { ModWarnings = prepared.ModWarnings }, CancellationToken.None).ConfigureAwait(false);
    }

    public async Task RestoreAsync(string dataDirectory, CancellationToken ct)
    {
        await installer.RestoreAsync(GameRoot(dataDirectory), ct).ConfigureAwait(false);
        await JsonFiles.WriteAsync(ReceiptPath(dataDirectory), new Receipt(GameRoot(dataDirectory), true, null, [], []), CancellationToken.None).ConfigureAwait(false);
    }

    public async Task<ProfileFileModHealth> InspectAsync(string dataDirectory, CancellationToken ct)
    {
        var inspection = await installer.InspectAsync(GameRoot(dataDirectory), ct).ConfigureAwait(false);
        var path = ReceiptPath(dataDirectory);
        var receipt = File.Exists(path) ? await JsonFiles.ReadAsync<Receipt>(path, ct).ConfigureAwait(false) : null;
        if (receipt is not null && (!string.Equals(receipt.GameDirectory, GameRoot(dataDirectory), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            receipt.AppliedModIds is null || receipt.Warnings is null || receipt.AppliedModIds.Any(id => id == Guid.Empty) ||
            receipt.Signature is not null && !PatchValidation.IsHash(receipt.Signature)))
            throw new InvalidDataException("Invalid file-mod deployment receipt.");
        var healthy = inspection.Complete && inspection.Files.All(file => file.Matches) && (receipt?.Complete ?? inspection.Files.Count == 0);
        var attributed = receipt?.ModWarnings ?? [];
        if (attributed.Count == 0 && receipt?.Warnings.Length > 0)
            attributed = receipt.AppliedModIds.ToDictionary(id => id, _ => receipt.Warnings);
        return new(healthy, receipt?.Signature, receipt?.AppliedModIds ?? [], receipt?.Warnings ?? []) { ModWarnings = attributed };
    }
}
