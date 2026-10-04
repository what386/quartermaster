using Quartermaster.Core.Patching;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public sealed record PatchSet(Guid Id, string Archive, int OriginalSlot, string Folder,
    IReadOnlyList<PatchFile> Files, IReadOnlyList<ResourceKey> Resources);
public sealed record Mod(Guid Id, string Name, string Description, string? Version, Guid? ManifestId,
    DateTimeOffset ImportedAt, IReadOnlyList<PatchSet> PatchSets, IReadOnlyList<ModOption> Options,
    IReadOnlyList<SourceReference> Sources);
public sealed record ModOptionImages(Guid OptionId, string? ImagePath, IReadOnlyList<ModChoiceImage> Choices);
public sealed record ModChoiceImage(string? ImagePath, string Description);
public sealed record LibraryState(int SchemaVersion, IReadOnlyList<Mod> Mods,
    IReadOnlyList<Profile> Profiles, Guid? ActiveProfileId)
{
    public const int CurrentSchemaVersion = 1;
    public IReadOnlyList<UpdateCheck> UpdateChecks { get; init; } = [];
    public static LibraryState Empty => new(CurrentSchemaVersion, [], [], null);
}

/// <summary>Provider identity only; network integrations and credentials live outside the library.</summary>
public sealed record SourceReference(string Provider, string ModId, string? FileId = null, string? InstalledVersion = null);
public sealed record UpdateCheck(Guid ModId, string Provider, DateTimeOffset CheckedAt,
    string? AvailableVersion, string? AvailableFileId, string? Error = null);
