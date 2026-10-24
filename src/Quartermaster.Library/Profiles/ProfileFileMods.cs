using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quartermaster.Core.Patching;
using Quartermaster.FileMerger;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library.Profiles;

public sealed record PreparedProfileFileMods(FileMergePlan Plan, string? Signature, IReadOnlyList<Guid> AppliedModIds, IReadOnlyList<string> Warnings);
public sealed record ProfileFileModHealth(bool Complete, string? Signature, IReadOnlyList<Guid> AppliedModIds, IReadOnlyList<string> Warnings)
{
    public static ProfileFileModHealth Empty => new(true, null, [], []);
}

/// <summary>Maps selected library variants to config merges in the same low-to-high order as archive patches.</summary>
public sealed class ProfileFileMods(ModContentStore contents)
{
    private readonly FileModInstaller installer = new(Path.Combine(contents.ApplicationDirectory, "filemods"));
    private sealed record Receipt(string GameDirectory, bool Complete, string? Signature, Guid[] AppliedModIds, string[] Warnings);
    private static string GameRoot(string dataDirectory) => Path.GetDirectoryName(Path.GetFullPath(dataDirectory))!;
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
                    var detected = new FileModDetector().DetectFile(file.RelativePath, bytes, ct);
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
                { throw new InvalidDataException($"Could not prepare file mod '{mod.Name}': {ex.Message}", ex); }
            }
        }
        var plan = await installer.PrepareAsync(GameRoot(dataDirectory), selected, ct).ConfigureAwait(false);
        var skipped = plan.Skipped.Select(skip => skip.Source).ToHashSet(StringComparer.Ordinal);
        var applied = selected.Where(mod => !skipped.Contains(mod.Source)).Select(mod => owners[mod.Source]).Distinct().ToArray();
        var warnings = plan.Conflicts.GroupBy(conflict => (conflict.Target, conflict.LosingMod, conflict.WinningMod))
            .Select(group => $"{group.Key.Target}: {group.Key.WinningMod} overrides {group.Key.LosingMod} ({group.Count()} conflicting values).")
            .Concat(plan.Skipped.Select(skip => $"{skip.Name}: {skip.Reason} ({skip.Target})")).ToArray();
        return new(plan, Signature(state, profile), applied, warnings);
    }

    public Task BeginAsync(PreparedProfileFileMods prepared, string dataDirectory, CancellationToken ct) =>
        JsonFiles.WriteAsync(ReceiptPath(dataDirectory), new Receipt(GameRoot(dataDirectory), false, prepared.Signature, prepared.AppliedModIds.ToArray(), prepared.Warnings.ToArray()), ct);

    public async Task ApplyAsync(PreparedProfileFileMods prepared, string dataDirectory, CancellationToken ct)
    {
        await installer.ApplyAsync(prepared.Plan, ct).ConfigureAwait(false);
        await JsonFiles.WriteAsync(ReceiptPath(dataDirectory), new Receipt(GameRoot(dataDirectory), true, prepared.Signature, prepared.AppliedModIds.ToArray(), prepared.Warnings.ToArray()), CancellationToken.None).ConfigureAwait(false);
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
        return new(healthy, receipt?.Signature, receipt?.AppliedModIds ?? [], receipt?.Warnings ?? []);
    }
}
