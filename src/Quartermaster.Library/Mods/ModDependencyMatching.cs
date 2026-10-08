namespace Quartermaster.Library.Mods;

/// <summary>Shared dependency identity rules for installation, details, and warnings.</summary>
public static class ModDependencyMatching
{
    // Keep provider identity separate so future cross-provider aliases/name matching
    // can satisfy requirements without copying metadata between different providers.
    public static bool Matches(Mod mod, ModDependency dependency) =>
        MatchesPage(mod, dependency) && (dependency.AllowedFileIds is null ||
            mod.Sources.Any(source => source.Provider == "nexusmods" && source.FileId is { } fileId && dependency.AllowedFileIds.Contains(fileId)))
        || dependency.Alternatives.Any(alternative => Matches(mod, alternative));

    public static bool MatchesPage(Mod mod, ModDependency dependency)
    {
        if (!Uri.TryCreate(dependency.Page, UriKind.Absolute, out var page)) return false;
        if (page.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase) || page.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase))
        {
            var parts = page.AbsolutePath.Trim('/').Split('/');
            if (parts is ["helldivers2", "mods", var id] && mod.Sources.Any(source => source.Provider == "nexusmods" && source.ModId == id)) return true;
        }
        return Uri.TryCreate(ModLinks.PageFor(mod), UriKind.Absolute, out var installedPage) &&
            page.Host.Replace("www.", "").Equals(installedPage.Host.Replace("www.", ""), StringComparison.OrdinalIgnoreCase) &&
            page.AbsolutePath.TrimEnd('/').Equals(installedPage.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

}
