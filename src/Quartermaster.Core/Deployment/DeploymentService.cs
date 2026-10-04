using Quartermaster.Core.Patching;

namespace Quartermaster.Core.Deployment;

/// <summary>Copies loadouts and records completion. Interrupted deployments require purge and redeploy.</summary>
public sealed class DeploymentService(IDeploymentStorage storage, IPatchRepairer? repairer = null)
{
    public async Task<DeploymentInspection> InspectAsync(string targetDirectory, CancellationToken ct = default)
    {
        await using var workspace = await storage.OpenAsync(targetDirectory, ct).ConfigureAwait(false);
        return await InspectUnlocked(workspace, ct).ConfigureAwait(false);
    }

    public async Task<DeploymentLedger> DeployAsync(DeploymentRequest request, string targetDirectory,
        DeploymentOptions? options = null, CancellationToken ct = default, IProgress<DeploymentProgress>? progress = null)
    {
        options ??= new();
        if (options.Repatch && repairer is null) throw new InvalidOperationException("Repatching requires a repair adapter.");
        await using var workspace = await storage.OpenAsync(targetDirectory, ct).ConfigureAwait(false);
        var inspection = await InspectUnlocked(workspace, ct).ConfigureAwait(false);
        if (inspection.NeedsPurge) throw new IOException("Deployment is incomplete or unknown. Purge patches, then redeploy.");
        var plan = DeploymentPlanner.Create(request);
        return await Execute(workspace, plan, options, ct, progress).ConfigureAwait(false);
    }

    public async Task PurgeAsync(string targetDirectory, CancellationToken ct = default)
    {
        await using var workspace = await storage.OpenAsync(targetDirectory, ct).ConfigureAwait(false);
        // Purge uses discovered filenames, never paths from an untrusted manifest.
        await workspace.WriteLedgerAsync(DeploymentLedger.Empty(workspace.TargetDirectory) with
        { Status = DeploymentStatus.Deploying }, ct).ConfigureAwait(false);
        DeletePatches(workspace, ct);
        workspace.ClearTemporaryFiles();
        await workspace.ClearLedgerAsync(ct).ConfigureAwait(false);
    }

    private static void DeletePatches(IDeploymentWorkspace workspace, CancellationToken ct)
    {
        foreach (var name in workspace.GetTargetFileNames().Where(n => PatchNames.TryParse(n, out _, out _, out _)))
        { ct.ThrowIfCancellationRequested(); workspace.DeleteTarget(name); }
    }

    private static bool Matches(FileFingerprint? actual, long size, string hash) => actual is not null && actual.Size == size &&
        actual.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase);

    private static async Task<DeploymentInspection> InspectUnlocked(IDeploymentWorkspace workspace, CancellationToken ct)
    {
        DeploymentLedger ledger;
        try
        {
            ledger = await workspace.ReadLedgerAsync(ct).ConfigureAwait(false) ?? DeploymentLedger.Empty(workspace.TargetDirectory);
            ValidateLedger(ledger, workspace);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NullReferenceException)
        { ledger = DeploymentLedger.Empty(workspace.TargetDirectory) with { Status = DeploymentStatus.Unknown }; }
        var health = new List<TrackedFile>();
        foreach (var owned in ledger.Files)
        {
            var actual = await workspace.FingerprintAsync(DeploymentArea.Target, owned.Name, ct).ConfigureAwait(false);
            health.Add(new(owned.Name, actual is null ? ManagedFileStatus.Missing :
                Matches(actual, owned.Size, owned.Sha256) ? ManagedFileStatus.Present : ManagedFileStatus.Modified));
        }
        var ownedNames = ledger.Files.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var untracked = workspace.GetTargetFileNames().Where(n => !ownedNames.Contains(n))
            .Select(n => PatchNames.TryParse(n, out var archive, out var slot, out _) ? new OccupiedSlot(archive, slot) : null)
            .OfType<OccupiedSlot>().Distinct().ToArray();
        return new(ledger, health.ToArray(), untracked);
    }

    private async Task<DeploymentLedger> Execute(IDeploymentWorkspace workspace,
        DeploymentPlan plan, DeploymentOptions options, CancellationToken ct, IProgress<DeploymentProgress>? progress)
    {
        workspace.BeginStaging();
        try
        {
            var files = new List<OwnedFile>();
            var mods = plan.Patches.GroupBy(patch => patch.SourceId).ToArray();
            var current = 0;
            foreach (var mod in mods)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(DeploymentPhase.Preparing, ++current, mods.Length, mod.Key));
                foreach (var patch in mod)
                foreach (var source in patch.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = PatchFiles.Name(patch.Archive, patch.Slot, source.Kind);
                    await workspace.StageAsync(patch.SourceId, source, name, ct).ConfigureAwait(false);
                    var staged = await workspace.FingerprintAsync(DeploymentArea.Staged, name, ct).ConfigureAwait(false);
                    if (!Matches(staged, source.Size, source.Sha256)) throw new IOException("Source file changed while staging.");
                    if (options.Repatch && source.Kind == PatchFileKind.Main)
                    {
                        var bytes = await workspace.ReadStagedAsync(name, ct).ConfigureAwait(false);
                        var result = await Task.Run(() => repairer!.Repair(bytes, ct), ct).ConfigureAwait(false);
                        if (result.RemovedUnits > 0 && !options.AllowRemovedUnits)
                            throw new InvalidDataException("Repair would remove missing units. Explicitly allow this or use an updated mod.");
                        await workspace.WriteStagedAsync(name, result.Data, ct).ConfigureAwait(false);
                        staged = await workspace.FingerprintAsync(DeploymentArea.Staged, name, ct).ConfigureAwait(false);
                    }
                    if (staged is null) throw new IOException("Staged file disappeared.");
                    files.Add(new(name, patch.Archive, patch.Slot, patch.SourceId, patch.PatchSetId, source.Kind,
                        staged.Size, staged.Sha256, source.Sha256));
                }
            }
            var ledger = new DeploymentLedger(1, workspace.TargetDirectory, plan.SelectionId == Guid.Empty ? null : plan.SelectionId,
                plan.Signature == "" ? null : plan.Signature, DateTimeOffset.UtcNow, files.ToArray())
            { SelectionName = plan.SelectionName };
            // Mark incomplete before the first game-directory mutation.
            await workspace.WriteLedgerAsync(ledger with { Status = DeploymentStatus.Deploying }, ct).ConfigureAwait(false);
            DeletePatches(workspace, ct);
            var operationId = Guid.NewGuid();
            current = 0;
            foreach (var mod in files.GroupBy(file => file.SourceId))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(DeploymentPhase.Deploying, ++current, mods.Length, mod.Key));
                foreach (var file in mod.OrderBy(f => f.Kind == PatchFileKind.Main))
                {
                    ct.ThrowIfCancellationRequested();
                    await workspace.PublishAsync(file.Name, operationId, ct).ConfigureAwait(false);
                }
            }
            current = 0;
            foreach (var mod in files.GroupBy(file => file.SourceId))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(DeploymentPhase.Verifying, ++current, mods.Length, mod.Key));
                foreach (var file in mod)
                {
                    ct.ThrowIfCancellationRequested();
                    await workspace.VerifyAsync(DeploymentArea.Target, file, ct).ConfigureAwait(false);
                }
            }
            ct.ThrowIfCancellationRequested();
            await workspace.WriteLedgerAsync(ledger, ct).ConfigureAwait(false);
            return ledger;
        }
        finally { workspace.ClearStaging(); }
    }

    private static void ValidateLedger(DeploymentLedger ledger, IDeploymentWorkspace workspace)
    {
        if (ledger.SchemaVersion != 1 || !Enum.IsDefined(ledger.Status) || !workspace.PathComparer.Equals(ledger.TargetDirectory, workspace.TargetDirectory))
            throw new InvalidDataException("Invalid deployment manifest target/version/status.");
        if (ledger.SelectionId is null ? ledger.Files.Count != 0 || ledger.Signature is not null :
            ledger.SelectionId == Guid.Empty || ledger.Signature is null || !PatchValidation.IsHash(ledger.Signature))
            throw new InvalidDataException("Invalid deployment selection.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in ledger.Files)
            if (!names.Add(file.Name) || !PatchNames.TryParse(file.Name, out var archive, out var slot, out var kind) ||
                file.Name != PatchFiles.Name(archive, slot, kind) || file.Archive != archive || file.Slot != slot || file.Kind != kind ||
                file.Size < 0 || !PatchValidation.IsHash(file.Sha256) || !PatchValidation.IsHash(file.SourceSha256))
                throw new InvalidDataException("Invalid owned-file ledger entry.");
    }
}
