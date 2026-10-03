using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Library.Importing;

namespace Quartermaster.Library.Storage;

public sealed record StoredRepair(Guid Id, Guid ModId, string GameDirectory, DateTimeOffset CreatedAt,
    string EngineVersion, string Directory, IReadOnlyList<OwnedFile> Files);
public sealed record RepairCatalog(int SchemaVersion, IReadOnlyList<StoredRepair> Repairs);

/// <summary>Stores the deployment manifest and repaired copies; temporary staging has no rollback backups.</summary>
public sealed class FileDeploymentStorage(string applicationDirectory, ModContentStore contents) : IDeploymentStorage
{
    private readonly string root = Path.GetFullPath(applicationDirectory);

    public async ValueTask<IDeploymentWorkspace> OpenAsync(string targetDirectory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = ManagedPaths.CanonicalDirectory(targetDirectory);
        if (!GameArchives.IsValidDataDirectory(target)) throw new ArgumentException("Target is not a Helldivers 2 data directory.");
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        // The JSON manifest is atomically replaced, so the exclusive process lease uses a separate file.
        var lease = await JsonFiles.LockAsync(ManagedPaths.Resolve(root, "library.lock"), ct).ConfigureAwait(false);
        return new Workspace(target, root, comparer, contents, lease);
    }

    private sealed class Workspace(string target, string storage, StringComparer comparer,
        ModContentStore contents, IAsyncDisposable lease) : IDeploymentWorkspace
    {
        public string TargetDirectory => target;
        public StringComparer PathComparer => comparer;
        private readonly string staging = ManagedPaths.Resolve(storage, "patched/.staging-" + Guid.NewGuid().ToString("N"));
        private string Manifest => ManagedPaths.Resolve(storage, "deployment.lock");
        private string FilePath(DeploymentArea area, string name) => ManagedPaths.Resolve(
            area == DeploymentArea.Target ? target : staging, name);
        public async Task<DeploymentLedger?> ReadLedgerAsync(CancellationToken ct) =>
            File.Exists(Manifest) ? await JsonFiles.ReadAsync<DeploymentLedger>(Manifest, ct).ConfigureAwait(false) : null;
        public async Task WriteLedgerAsync(DeploymentLedger ledger, CancellationToken ct)
        {
            await JsonFiles.WriteAsync(Manifest, ledger, ct).ConfigureAwait(false);
            await new JsonEventLog(storage).AppendAsync("deployment", ledger.Status.ToString(), target, CancellationToken.None).ConfigureAwait(false);
        }
        public async Task ClearLedgerAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ManagedPaths.CheckLink(Manifest);
            File.Delete(Manifest);
            await new JsonEventLog(storage).AppendAsync("purge", "complete", target, CancellationToken.None).ConfigureAwait(false);
        }
        public IReadOnlyList<string> GetTargetFileNames() => Directory.EnumerateFiles(target).Select(Path.GetFileName).OfType<string>().ToArray();
        public async Task<FileFingerprint?> FingerprintAsync(DeploymentArea area, string name, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var path = FilePath(area, name);
            return File.Exists(path) ? new(new FileInfo(path).Length, await FileIntegrity.HashAsync(path, ct).ConfigureAwait(false)) : null;
        }
        public void BeginStaging() => Directory.CreateDirectory(staging);
        public async Task StageAsync(Guid modId, PatchFile file, string name, CancellationToken ct)
        {
            var original = contents.GetFilePath(modId, file);
            await FileIntegrity.VerifyAsync(original, file.Size, file.Sha256, ct).ConfigureAwait(false);
            await CopyAsync(original, FilePath(DeploymentArea.Staged, name), ct).ConfigureAwait(false);
        }
        public Task<byte[]> ReadStagedAsync(string name, CancellationToken ct) => File.ReadAllBytesAsync(FilePath(DeploymentArea.Staged, name), ct);
        public Task WriteStagedAsync(string name, byte[] data, CancellationToken ct) => File.WriteAllBytesAsync(FilePath(DeploymentArea.Staged, name), data, ct);
        public async Task StoreRepairsAsync(IReadOnlyList<OwnedFile> files, CancellationToken ct)
        {
            if (files.Count == 0) return;
            var catalogPath = ManagedPaths.Resolve(storage, "patches.json");
            var catalog = File.Exists(catalogPath) ? await JsonFiles.ReadAsync<RepairCatalog>(catalogPath, ct).ConfigureAwait(false) : new(1, []);
            if (catalog.SchemaVersion != 1 || catalog.Repairs is null) throw new InvalidDataException("Invalid repair catalog.");
            var added = new List<StoredRepair>();
            var saved = false;
            try
            {
                foreach (var group in files.GroupBy(f => f.SourceId))
                {
                    var id = Guid.NewGuid();
                    var relative = $"patched/{group.Key:N}/{id:N}";
                    var directory = ManagedPaths.Resolve(storage, relative);
                    var entry = new StoredRepair(id, group.Key, target, DateTimeOffset.UtcNow,
                        typeof(global::Quartermaster.Repatcher.Repatcher).Assembly.GetName().Version?.ToString() ?? "unknown", relative, group.ToArray());
                    added.Add(entry);
                    Directory.CreateDirectory(directory);
                    foreach (var file in group)
                    {
                        var destination = ManagedPaths.Resolve(directory, file.Name);
                        await CopyAsync(FilePath(DeploymentArea.Staged, file.Name), destination, ct).ConfigureAwait(false);
                        await FileIntegrity.VerifyAsync(destination, file.Size, file.Sha256, ct).ConfigureAwait(false);
                    }
                }
                await JsonFiles.WriteAsync(catalogPath, catalog with { Repairs = [.. catalog.Repairs, .. added] }, ct).ConfigureAwait(false);
                saved = true;
            }
            finally
            {
                if (!saved) foreach (var entry in added)
                {
                    var directory = ManagedPaths.Resolve(storage, entry.Directory);
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
            }
        }
        public Task VerifyAsync(DeploymentArea area, OwnedFile file, CancellationToken ct) => FileIntegrity.VerifyAsync(FilePath(area, file.Name), file.Size, file.Sha256, ct);
        public void DeleteTarget(string name) => File.Delete(FilePath(DeploymentArea.Target, name));
        public async Task PublishAsync(string name, Guid operationId, CancellationToken ct)
        {
            var temporary = ManagedPaths.Resolve(target, $".quartermaster-{operationId:N}-{name}.tmp");
            try
            {
                await CopyAsync(FilePath(DeploymentArea.Staged, name), temporary, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                File.Move(temporary, FilePath(DeploymentArea.Target, name), overwrite: false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void ClearTemporaryFiles()
        {
            foreach (var name in GetTargetFileNames())
            {
                if (!name.StartsWith(".quartermaster-", StringComparison.Ordinal) || !name.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                var remainder = name[15..^4];
                if (remainder.Length > 33 && Guid.TryParseExact(remainder[..32], "N", out _) && remainder[32] == '-' &&
                    PatchNames.TryParse(remainder[33..], out _, out _, out _)) DeleteTarget(name);
            }
            var repaired = ManagedPaths.Resolve(storage, "patched");
            if (Directory.Exists(repaired)) foreach (var directory in Directory.EnumerateDirectories(repaired, ".staging-*"))
            {
                var name = Path.GetFileName(directory);
                if (!Guid.TryParseExact(name[9..], "N", out _)) continue;
                _ = ManagedPaths.Enumerate(directory).ToArray();
                Directory.Delete(directory, recursive: true);
            }
        }
        public void ClearStaging()
        {
            if (!Directory.Exists(staging)) return;
            _ = ManagedPaths.Enumerate(staging).ToArray();
            Directory.Delete(staging, recursive: true);
        }
        public async ValueTask DisposeAsync()
        {
            try { ClearStaging(); }
            finally { await lease.DisposeAsync().ConfigureAwait(false); }
        }
        private static async Task CopyAsync(string source, string destination, CancellationToken ct)
        {
            await using var input = File.OpenRead(source);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct).ConfigureAwait(false);
            await output.FlushAsync(ct).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
    }
}
