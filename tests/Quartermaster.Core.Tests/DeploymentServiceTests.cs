using Quartermaster.Core.Patching;
using System.Security.Cryptography;
using Quartermaster.Core.Deployment;
using Xunit;

namespace Quartermaster.Core.Tests;

public class DeploymentServiceTests
{
    private const string Archive = "9ba626afa44a3aa3";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static DeploymentRequest Create(byte value, MemoryStorage storage)
    {
        var main = new byte[] { value }; var stream = new byte[] { (byte)(value + 1) };
        var files = new PatchFile[] { new("source.patch_0", PatchFileKind.Main, 1, Hash(main)), new("source.patch_0.stream", PatchFileKind.Stream, 1, Hash(stream)) };
        var sourceId = Guid.NewGuid();
        storage.Content[(sourceId, files[0].RelativePath)] = main;
        storage.Content[(sourceId, files[1].RelativePath)] = stream;
        return new(Guid.NewGuid(), [new(sourceId, Guid.NewGuid(), Archive, files, [])]);
    }

    [Fact]
    public async Task CoreSequencesDeploymentAndPurgeWithoutFilesystemAccess()
    {
        var storage = new MemoryStorage(); var request = Create(1, storage);
        var service = new DeploymentService(storage);
        var ledger = await service.DeployAsync(request, "game");
        Assert.All(ledger.Files, f => Assert.Equal(0, f.Slot));
        Assert.Equal(new[] { "Deploying", "Complete" }, storage.Events.Where(e => e.StartsWith("Manifest:")).Select(e => e[9..]));
        Assert.Equal(new[] { Archive + ".patch_0.stream", Archive + ".patch_0" }, storage.Events.Where(e => e.StartsWith("Publish:")).Select(e => e[8..]));
        await service.PurgeAsync("game");
        Assert.Empty(storage.Target); Assert.Null(storage.Ledger);
    }

    [Fact]
    public async Task FailedPublicationLeavesIncompleteManifestUntilPurge()
    {
        var storage = new MemoryStorage(); var old = Create(1, storage);
        var service = new DeploymentService(storage);
        var ledger = await service.DeployAsync(old, "game");
        var replacement = Create(4, storage); storage.FailNextPublish = true;
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(replacement, "game"));
        Assert.Equal(DeploymentStatus.Deploying, storage.Ledger!.Status);
        Assert.NotEqual(ledger.Signature, storage.Ledger.Signature);
        Assert.True((await service.InspectAsync("game")).NeedsPurge);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(replacement, "game"));
        await service.PurgeAsync("game");
        Assert.Null(storage.Ledger); Assert.Empty(storage.Target);
        await service.DeployAsync(replacement, "game");
        Assert.Equal(new byte[] { 4 }, storage.Target[Archive + ".patch_0"]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAfterPublicationLeavesIncompleteManifest(int copiedFiles)
    {
        var storage = new MemoryStorage(); var request = Create(1, storage);
        using var cancellation = new CancellationTokenSource();
        var published = 0;
        storage.AfterPublish = () => { if (++published == copiedFiles) cancellation.Cancel(); };
        var service = new DeploymentService(storage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeployAsync(request, "game", ct: cancellation.Token));
        Assert.Equal(DeploymentStatus.Deploying, storage.Ledger!.Status);
        Assert.Equal(copiedFiles, storage.Target.Count);
        Assert.True((await service.InspectAsync("game")).NeedsPurge);
        await service.PurgeAsync("game");
        Assert.Null(storage.Ledger); Assert.Empty(storage.Target);
    }

    private sealed class MemoryStorage : IDeploymentStorage, IDeploymentWorkspace
    {
        public Dictionary<(Guid Mod, string Path), byte[]> Content { get; } = [];
        public Dictionary<string, byte[]> Target { get; } = [];
        private Dictionary<string, byte[]> Staged { get; } = [];
        public List<string> Events { get; } = [];
        public DeploymentLedger? Ledger { get; private set; }
        public bool FailNextPublish { get; set; }
        public Action? AfterPublish { get; set; }
        public string TargetDirectory => "game";
        public StringComparer PathComparer => StringComparer.Ordinal;
        public ValueTask<IDeploymentWorkspace> OpenAsync(string targetDirectory, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult<IDeploymentWorkspace>(this); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<DeploymentLedger?> ReadLedgerAsync(CancellationToken ct) => Task.FromResult(Ledger);
        public Task WriteLedgerAsync(DeploymentLedger ledger, CancellationToken ct)
        { Ledger = ledger; Events.Add("Manifest:" + ledger.Status); return Task.CompletedTask; }
        public Task ClearLedgerAsync(CancellationToken ct) { Ledger = null; return Task.CompletedTask; }
        public IReadOnlyList<string> GetTargetFileNames() => Target.Keys.ToArray();
        private Dictionary<string, byte[]> Area(DeploymentArea area) => area switch
        { DeploymentArea.Target => Target, DeploymentArea.Staged => Staged, _ => throw new ArgumentOutOfRangeException(nameof(area)) };
        public Task<FileFingerprint?> FingerprintAsync(DeploymentArea area, string name, CancellationToken ct) =>
            Task.FromResult(Area(area).TryGetValue(name, out var bytes) ? new FileFingerprint(bytes.Length, Hash(bytes)) : null);
        public void BeginStaging() => ClearStaging();
        public Task StageAsync(Guid modId, PatchFile file, string name, CancellationToken ct)
        { Staged.Add(name, Content[(modId, file.RelativePath)].ToArray()); return Task.CompletedTask; }
        public Task<byte[]> ReadStagedAsync(string name, CancellationToken ct) => Task.FromResult(Staged[name].ToArray());
        public Task WriteStagedAsync(string name, byte[] data, CancellationToken ct) { Staged[name] = data.ToArray(); return Task.CompletedTask; }
        public Task StoreRepairsAsync(IReadOnlyList<OwnedFile> files, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(DeploymentArea area, OwnedFile file, CancellationToken ct)
        {
            if (!Area(area).TryGetValue(file.Name, out var bytes) || bytes.Length != file.Size || Hash(bytes) != file.Sha256) throw new IOException("Changed file.");
            return Task.CompletedTask;
        }
        public void DeleteTarget(string name) => Target.Remove(name);
        public Task PublishAsync(string name, Guid operationId, CancellationToken ct)
        {
            if (FailNextPublish) { FailNextPublish = false; throw new IOException("Simulated publication failure."); }
            Target.Add(name, Staged[name].ToArray()); Events.Add("Publish:" + name); AfterPublish?.Invoke(); return Task.CompletedTask;
        }
        public void ClearTemporaryFiles() { }
        public void ClearStaging() => Staged.Clear();
    }
}
