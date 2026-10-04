using Quartermaster.Core.Patching;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quartermaster.Core.Deployment;

public sealed record OccupiedSlot(string Archive, int Slot);
public sealed record PlannedPatch(Guid SourceId, Guid PatchSetId, string Archive, int Slot, IReadOnlyList<PatchFile> Files)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionHash { get; init; }
}
public sealed record DeploymentPlan(Guid SelectionId, string Signature, IReadOnlyList<PlannedPatch> Patches)
{
    public string? SelectionName { get; init; }
}
public sealed record OwnedFile(string Name, string Archive, int Slot, [property: JsonPropertyName("modId")] Guid SourceId, Guid PatchSetId,
    PatchFileKind Kind, long Size, string Sha256, string SourceSha256)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectionHash { get; init; }
}
public sealed record DeploymentLedger(int SchemaVersion, string TargetDirectory, [property: JsonPropertyName("profileId")] Guid? SelectionId,
    [property: JsonPropertyName("selectionHash")] string? Signature, DateTimeOffset? DeployedAt, IReadOnlyList<OwnedFile> Files)
{
    [JsonRequired]
    public DeploymentStatus Status { get; init; } = DeploymentStatus.Complete;
    [JsonPropertyName("profileName")]
    public string? SelectionName { get; init; }
    public IReadOnlyList<Guid> Mods => Files.Select(f => f.SourceId).Distinct().ToArray();
    public static DeploymentLedger Empty(string target) => new(1, target, null, null, null, []);
}

public enum DeploymentStatus { Complete, Deploying, Unknown }

public static class DeploymentPlanner
{
    public static DeploymentPlan Create(DeploymentRequest request)
    {
        PatchValidation.Validate(request);
        var nextSlots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var patches = new List<PlannedPatch>();
        foreach (var patch in request.Patches)
        {
            var slot = nextSlots.GetValueOrDefault(patch.Archive);
            nextSlots[patch.Archive] = checked(slot + 1);
            patches.Add(new(patch.SourceId, patch.PatchSetId, patch.Archive.ToLowerInvariant(), slot, patch.Files.ToArray()) { SelectionHash = patch.SelectionHash });
        }
        var signature = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        { request.SelectionId, Patches = patches })))).ToLowerInvariant();
        return new(request.SelectionId, signature, patches.ToArray()) { SelectionName = request.SelectionName };
    }
}

public sealed record ArchiveOverlap(string Archive, IReadOnlyList<Guid> SourceIds);
public sealed record ResourceConflict(string Archive, ResourceKey Resource, IReadOnlyList<Guid> SourceIds, Guid WinningSourceId);
public sealed record ConflictReport(IReadOnlyList<ArchiveOverlap> Archives, IReadOnlyList<ResourceConflict> Resources);

public static class ConflictAnalyzer
{
    public static ConflictReport Analyze(DeploymentRequest request)
    {
        PatchValidation.Validate(request);
        var archives = request.Patches.GroupBy(p => p.Archive.ToLowerInvariant()).Select(g =>
            new ArchiveOverlap(g.Key, g.Select(p => p.SourceId).Distinct().ToArray())).Where(g => g.SourceIds.Count > 1).ToArray();
        var resources = request.Patches.SelectMany(p => p.Resources.Select(r => (p.SourceId, Archive: p.Archive.ToLowerInvariant(), Resource: r)))
            .GroupBy(p => (p.Archive, p.Resource)).Select(g =>
            {
                var ids = g.Select(p => p.SourceId).Distinct().ToArray();
                return new ResourceConflict(g.Key.Archive, g.Key.Resource, ids, g.Last().SourceId);
            }).Where(g => g.SourceIds.Count > 1).ToArray();
        return new(archives, resources);
    }
}

public enum ManagedFileStatus { Present, Missing, Modified }
public sealed record TrackedFile(string Name, ManagedFileStatus Status);
