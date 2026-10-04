using Quartermaster.Core.Deployment;
using Quartermaster.Core.Patching;
using Quartermaster.Library.Mods;

namespace Quartermaster.Library.Profiles;

/// <summary>Resolves library-specific profile choices into Core's ordered patch input.</summary>
public static class ProfilePatches
{
    public static DeploymentRequest Resolve(LibraryState state, Guid profileId)
    {
        StateValidation.Validate(state);
        var profile = state.Profiles.SingleOrDefault(p => p.Id == profileId) ?? throw new KeyNotFoundException("Profile does not exist.");
        return Resolve(state, profile);
    }

    public static DeploymentRequest Resolve(LibraryState state, Profile profile)
    {
        StateValidation.Validate(state with { Profiles = [.. state.Profiles.Where(p => p.Id != profile.Id), profile] });
        var mods = state.Mods.ToDictionary(m => m.Id);
        var patches = ProfileEditor.InDeploymentOrder(profile).Where(e => e.Enabled)
            .SelectMany(e => PatchSelection.Select(mods[e.ModId], e).Select(p =>
                new SelectedPatch(e.ModId, p.Id, p.Archive, p.Files.ToArray(), p.Resources.ToArray())
                { SelectionHash = PatchSelection.OptionsHash(mods[e.ModId], e) })).ToArray();
        return new(profile.Id, patches) { SelectionName = profile.Name };
    }
}

/// <summary>Connects profiles to Core's deployment service; Core owns execution and deployment health.</summary>
public sealed class ProfileDeploymentService(DeploymentService deployment)
{
    public Task<DeploymentLedger> DeployAsync(LibraryState state, Guid profileId, string targetDirectory,
        DeploymentOptions? options = null, CancellationToken ct = default, IProgress<DeploymentProgress>? progress = null) =>
        deployment.DeployAsync(ProfilePatches.Resolve(state, profileId), targetDirectory, options, ct, progress);
    public Task<DeploymentInspection> InspectAsync(string targetDirectory, CancellationToken ct = default) => deployment.InspectAsync(targetDirectory, ct);
    public Task PurgeAsync(string targetDirectory, CancellationToken ct = default) => deployment.PurgeAsync(targetDirectory, ct);
}

public enum ModDeploymentStatus { NotDeployed, Deployed, DifferentSelection, Damaged }

public static class DeploymentTracking
{
    public static ModDeploymentStatus ForMod(Guid modId, DeploymentPlan plan,
        DeploymentLedger ledger, IReadOnlyList<TrackedFile> health)
    {
        var deployed = ledger.Files.Where(f => f.SourceId == modId).ToArray();
        var selected = plan.Patches.Where(p => p.SourceId == modId).SelectMany(p => p.Files.Select(f =>
            (Name: PatchFiles.Name(p.Archive, p.Slot, f.Kind), p.PatchSetId, f.Kind, f.Sha256, p.SelectionHash))).ToArray();
        if (deployed.Length == 0) return ModDeploymentStatus.NotDeployed;
        if (ledger.Status != DeploymentStatus.Complete || deployed.Any(f => health.All(h => h.Name != f.Name || h.Status != ManagedFileStatus.Present))) return ModDeploymentStatus.Damaged;
        return selected.Length == deployed.Length && selected.All(s => deployed.Any(f => f.Name == s.Name && f.PatchSetId == s.PatchSetId &&
            f.Kind == s.Kind && f.SourceSha256.Equals(s.Sha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(f.SelectionHash, s.SelectionHash, StringComparison.OrdinalIgnoreCase)))
            ? ModDeploymentStatus.Deployed : ModDeploymentStatus.DifferentSelection;
    }
}
