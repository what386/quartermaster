using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Library.Importing;

namespace Quartermaster.Library.Storage;

/// <summary>Stores the deployment manifest and stages files temporarily before publication.</summary>
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
        private readonly string staging = ManagedPaths.Resolve(storage, ".staging-" + Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(storage)) foreach (var directory in Directory.EnumerateDirectories(storage, ".staging-*"))
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
