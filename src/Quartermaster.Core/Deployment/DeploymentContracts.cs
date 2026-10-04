using Quartermaster.Core.Patching;

namespace Quartermaster.Core.Deployment;

public enum DeploymentArea { Target, Staged }
public sealed record FileFingerprint(long Size, string Sha256);
public sealed record DeploymentOptions(bool Repatch = false, bool AllowRemovedUnits = false);
public sealed record DeploymentInspection(DeploymentLedger Ledger, IReadOnlyList<TrackedFile> Files, IReadOnlyList<OccupiedSlot> UntrackedSlots)
{
    public bool NeedsPurge => Ledger.Status != DeploymentStatus.Complete || UntrackedSlots.Count > 0 ||
        Files.Any(f => f.Status != ManagedFileStatus.Present);
}

/// <summary>Opens an exclusively locked workspace for a target. Implementations own filesystem access.</summary>
public interface IDeploymentStorage
{
    ValueTask<IDeploymentWorkspace> OpenAsync(string targetDirectory, CancellationToken ct = default);
}

/// <summary>Filesystem primitives scoped to one deployment target. Core determines their order and policy.</summary>
public interface IDeploymentWorkspace : IAsyncDisposable
{
    string TargetDirectory { get; }
    StringComparer PathComparer { get; }
    Task<DeploymentLedger?> ReadLedgerAsync(CancellationToken ct);
    Task WriteLedgerAsync(DeploymentLedger ledger, CancellationToken ct);
    Task ClearLedgerAsync(CancellationToken ct);
    IReadOnlyList<string> GetTargetFileNames();
    Task<FileFingerprint?> FingerprintAsync(DeploymentArea area, string name, CancellationToken ct);
    void BeginStaging();
    Task StageAsync(Guid sourceId, PatchFile file, string name, CancellationToken ct);
    Task<byte[]> ReadStagedAsync(string name, CancellationToken ct);
    Task WriteStagedAsync(string name, byte[] data, CancellationToken ct);
    Task VerifyAsync(DeploymentArea area, OwnedFile file, CancellationToken ct);
    void DeleteTarget(string name);
    Task PublishAsync(string name, Guid operationId, CancellationToken ct);
    void ClearStaging();
    void ClearTemporaryFiles();
}

public sealed record RepairedPatch(byte[] Data, int RemovedUnits);
public interface IPatchRepairer
{
    RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default);
}
