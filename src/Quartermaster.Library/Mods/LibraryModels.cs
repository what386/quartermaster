using Quartermaster.Core.Patching;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public sealed record PatchSet(Guid Id, string Archive, int OriginalSlot, string Folder,
    IReadOnlyList<PatchFile> Files, IReadOnlyList<ResourceKey> Resources);
public sealed record Mod(Guid Id, string Name, string Description, string? Version, Guid? ManifestId,
    DateTimeOffset ImportedAt, IReadOnlyList<PatchSet> PatchSets, IReadOnlyList<ModOption> Options,
    IReadOnlyList<SourceReference> Sources)
{
    // Distinguish a checked, empty requirement list from an older import with no metadata.
    public bool DependenciesKnown { get; init; }
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    public string? PageLink { get; init; }
    public string? ImportedFileName { get; init; }
    public bool Superseded { get; init; }
}

public sealed record ModDependency(string Name, string Page, string? Notes = null, bool CanInstall = true);

public static class ModLinks
{
    public static string? PageFor(Mod mod) => mod.PageLink ?? mod.Sources.Select(source => source.Provider switch
    {
        "nexusmods" when long.TryParse(source.ModId, out var id) && id > 0 => $"https://www.nexusmods.com/helldivers2/mods/{id}",
        "github" when source.ModId.Split('/') is [var owner, var repo] && owner.Length > 0 && repo.Length > 0
            => $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}",
        _ => null
    }).FirstOrDefault(link => link is not null);

    public static string? ValidatePage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo != "" || !uri.IsDefaultPort)
            throw new ArgumentException("Enter a public HTTPS mod page link.");
        return uri.AbsoluteUri;
    }
}
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

/// <summary>Import identity uses patch contents and options, independent of generated IDs and ZIP wrapper folders.</summary>
internal static class ModIdentity
{
    public static string GetKey(Mod mod)
    {
        var folders = mod.PatchSets.Select(set => set.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var prefix = 0;
        while (folders.Length > 0 && folders.All(folder => folder.Length > prefix) && folders.All(folder => folder[prefix] == folders[0][prefix])) prefix++;
        var keys = mod.PatchSets.ToDictionary(set => set.Id, set => global::System.Text.Json.JsonSerializer.Serialize(new
        {
            Folder = string.Join('/', set.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(prefix)),
            Archive = set.Archive.ToLowerInvariant(),
            set.OriginalSlot,
            Files = set.Files.OrderBy(file => file.Kind).Select(file => new { file.Kind, file.Size, Hash = file.Sha256.ToUpperInvariant() })
        }));
        return global::System.Text.Json.JsonSerializer.Serialize(new
        {
            mod.ManifestId,
            mod.Version,
            Patches = keys.Values.Order(StringComparer.Ordinal),
            Options = mod.Options.Select(option => new
            {
                option.Name,
                option.Description,
                Patches = option.PatchSetIds.Select(id => keys[id]),
                Choices = option.Choices.Select(choice => new { choice.Name, Patches = choice.PatchSetIds.Select(id => keys[id]) })
            })
        });
    }
}
