using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Storage;

public enum StorageCleanupKind { PreviousVersion, OrphanedContent, AbandonedStaging }
public sealed record StorageCandidate(string RelativePath, string Name, StorageCleanupKind Kind, long Bytes, DateTime ModifiedAtUtc, Guid? ModId = null);
public sealed record ModStorageUsage(Guid ModId, string Name, string? Version, long Bytes, int ProfileCount, bool IsDeployed, bool IsPreviousVersion);
public sealed record StorageReport(long LibraryBytes, long TemporaryBytes, long OtherBytes, IReadOnlyList<ModStorageUsage> Mods, IReadOnlyList<StorageCandidate> Candidates)
{
    public long DeployedBytes { get; init; }
    public bool IsDeploymentUncertain { get; init; }
    public long TotalBytes => LibraryBytes + TemporaryBytes + OtherBytes;
    public long ReclaimableBytes => Candidates.Sum(item => item.Bytes);
}
public sealed record StorageCleanupResult(long RemovedBytes, int RemovedItems, int SkippedItems);

/// <summary>Measures owned storage and removes only reviewed, still-unreferenced content under the library lease.</summary>
public sealed class StorageManager(string applicationDirectory)
{
    private readonly string root = Path.GetFullPath(applicationDirectory);
    private readonly JsonLibraryStore store = new(applicationDirectory);
    private static readonly TimeSpan StagingRetention = TimeSpan.FromDays(1);

    /// <summary>Read-only settings overview; it never acquires the mutation lease. Cleanup always rechecks under that lease.</summary>
    public Task<StorageReport> ReadOverviewAsync(LibraryState state, CancellationToken ct = default) => ScanAsync(state, ct);

    public async Task<StorageReport> ScanAsync(CancellationToken ct = default)
    {
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        return await ScanAsync(state, ct).ConfigureAwait(false);
    }

    public async Task<StorageCleanupResult> CleanupAsync(IReadOnlyCollection<StorageCandidate> reviewed, CancellationToken ct = default)
    {
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        var fresh = await ScanAsync(state, ct).ConfigureAwait(false);
        // Compare the entire snapshot: changed contents or newly protected items require another review.
        var requested = reviewed.Distinct().ToArray();
        var candidates = requested.Where(item => fresh.Candidates.Contains(item)).ToArray();
        var removedMods = candidates.Where(item => item.ModId is not null).Select(item => item.ModId!.Value).ToHashSet();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var path = ManagedPaths.Resolve(root, candidate.RelativePath);
            _ = ManagedPaths.Enumerate(path).ToArray();
        }
        if (removedMods.Count > 0)
            await store.SaveAsync(state with
            {
                Mods = state.Mods.Where(mod => !removedMods.Contains(mod.Id)).ToArray(),
                UpdateChecks = state.UpdateChecks.Where(check => !removedMods.Contains(check.ModId)).ToArray()
            }, ct).ConfigureAwait(false);
        long removedBytes = 0;
        var removedItems = 0;
        foreach (var candidate in candidates)
        {
            // Once metadata is committed, finish deleting its content even if cancellation arrives.
            var path = ManagedPaths.Resolve(root, candidate.RelativePath);
            if (Directory.Exists(path))
            {
                _ = ManagedPaths.Enumerate(path).ToArray();
                Directory.Delete(path, recursive: true);
            }
            removedBytes += candidate.Bytes;
            removedItems++;
        }
        return new(removedBytes, removedItems, requested.Length - candidates.Length);
    }

    private async Task<StorageReport> ScanAsync(LibraryState state, CancellationToken ct)
    {
        var ledgerPath = ManagedPaths.Resolve(root, "deployment.lock");
        // An unreadable ledger fails the scan rather than guessing which originals are deployed.
        var ledger = File.Exists(ledgerPath) ? await JsonFiles.ReadAsync<DeploymentLedger>(ledgerPath, ct).ConfigureAwait(false) : null;
        if (ledger is not null && (ledger.SchemaVersion != 1 || ledger.Files is null || !Enum.IsDefined(ledger.Status) ||
            string.IsNullOrWhiteSpace(ledger.TargetDirectory) || !Path.IsPathFullyQualified(ledger.TargetDirectory)))
            throw new InvalidDataException("Invalid deployment ledger.");
        var uncertain = ledger is not null && ledger.Status != DeploymentStatus.Complete;
        var deployed = ledger?.Mods.ToHashSet() ?? [];
        if (ledger is not null && Directory.Exists(ledger.TargetDirectory))
        {
            var fileMods = await new ProfileFileMods(new ModContentStore(root)).InspectAsync(ledger.TargetDirectory, ct).ConfigureAwait(false);
            deployed.UnionWith(fileMods.AppliedModIds);
            uncertain |= !fileMods.Complete;
        }
        long deployedBytes = 0;
        if (ledger is not null)
            foreach (var file in ledger.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = ManagedPaths.Resolve(ledger.TargetDirectory, file.Name);
                if (File.Exists(path)) deployedBytes += new FileInfo(path).Length;
            }
        var referenced = state.Profiles.SelectMany(profile => profile.Entries).Select(entry => entry.ModId).ToHashSet();
        var candidates = new List<StorageCandidate>();
        var mods = new List<ModStorageUsage>();
        foreach (var mod in state.Mods)
        {
            ct.ThrowIfCancellationRequested();
            var relative = $"library/{mod.Id:N}";
            var usage = Measure(ManagedPaths.Resolve(root, relative), ct);
            mods.Add(new(mod.Id, mod.Name, mod.Version, usage.Bytes,
                state.Profiles.Count(profile => profile.Entries.Any(entry => entry.ModId == mod.Id)), deployed.Contains(mod.Id), mod.Superseded));
            if (!uncertain && mod.Superseded && !referenced.Contains(mod.Id) && !deployed.Contains(mod.Id) && Directory.Exists(ManagedPaths.Resolve(root, relative)))
                candidates.Add(new(relative, mod.Name, StorageCleanupKind.PreviousVersion, usage.Bytes, usage.ModifiedAtUtc, mod.Id));
        }
        var library = ManagedPaths.Resolve(root, "library");
        if (Directory.Exists(library))
        {
            var known = state.Mods.Select(mod => mod.Id).ToHashSet();
            foreach (var path in Directory.EnumerateDirectories(library))
            {
                ct.ThrowIfCancellationRequested();
                ManagedPaths.CheckLink(path);
                if (uncertain || !Guid.TryParseExact(Path.GetFileName(path), "N", out var id) || known.Contains(id) || deployed.Contains(id)) continue;
                var usage = Measure(path, ct);
                candidates.Add(new($"library/{Path.GetFileName(path)}", Path.GetFileName(path), StorageCleanupKind.OrphanedContent, usage.Bytes, usage.ModifiedAtUtc));
            }
        }
        var temporary = ManagedPaths.Resolve(root, "temp");
        if (Directory.Exists(temporary))
        {
            foreach (var path in Directory.EnumerateDirectories(temporary))
            {
                ct.ThrowIfCancellationRequested();
                ManagedPaths.CheckLink(path);
                var name = Path.GetFileName(path);
                if (!IsStagingDirectory(name)) continue;
                var usage = Measure(path, ct);
                if (usage.ModifiedAtUtc > DateTime.UtcNow - StagingRetention) continue;
                candidates.Add(new($"temp/{name}", name, StorageCleanupKind.AbandonedStaging, usage.Bytes, usage.ModifiedAtUtc));
            }
        }
        var libraryBytes = Measure(library, ct).Bytes;
        var temporaryBytes = Measure(temporary, ct).Bytes;
        long otherBytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            ct.ThrowIfCancellationRequested();
            if (path == library || path == temporary) continue;
            ManagedPaths.CheckLink(path);
            otherBytes += Directory.Exists(path) ? Measure(path, ct).Bytes : new FileInfo(path).Length;
        }
        return new(libraryBytes, temporaryBytes, otherBytes, mods.OrderByDescending(mod => mod.Bytes).ToArray(),
            candidates.OrderByDescending(item => item.Bytes).ToArray())
        { DeployedBytes = deployedBytes, IsDeploymentUncertain = uncertain };
    }

    private static bool IsStagingDirectory(string name) => new[] { "import-", "profile-", "deployment-" }
        .Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParseExact(name[prefix.Length..], "N", out _));

    private static (long Bytes, DateTime ModifiedAtUtc) Measure(string path, CancellationToken ct)
    {
        ManagedPaths.CheckLink(path);
        if (!Directory.Exists(path)) return (0, DateTime.MinValue);
        long bytes = 0;
        var modified = Directory.GetLastWriteTimeUtc(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            ct.ThrowIfCancellationRequested();
            ManagedPaths.CheckLink(entry);
            if (Directory.Exists(entry))
            {
                var child = Measure(entry, ct);
                bytes += child.Bytes;
                if (child.ModifiedAtUtc > modified) modified = child.ModifiedAtUtc;
            }
            else
            {
                var file = new FileInfo(entry);
                bytes += file.Length;
                if (file.LastWriteTimeUtc > modified) modified = file.LastWriteTimeUtc;
            }
        }
        return (bytes, modified);
    }
}
