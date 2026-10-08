namespace Quartermaster.Library.Mods;

/// <summary>Mod managers advertised as requirements are not game dependencies.</summary>
public static class ModDependencyExclusions
{
    private static readonly HashSet<long> NexusModIds =
    [
        4664, // HD2 Arsenal
        109   // HD2ModManager
    ];

    public static bool IsExcluded(Uri page) =>
        (page.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase) ||
         page.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase)) &&
        page.AbsolutePath.Trim('/').Split('/') is ["helldivers2", "mods", var id] &&
        long.TryParse(id, out var modId) && NexusModIds.Contains(modId);

    public static bool IsExcluded(ModDependency dependency) =>
        Uri.TryCreate(dependency.Page, UriKind.Absolute, out var page) && IsExcluded(page) ||
        dependency.Alternatives.Any(IsExcluded);

    // Alternatives are OR requirements: an excluded manager makes the whole requirement unnecessary.
    public static IReadOnlyList<ModDependency> Filter(IReadOnlyList<ModDependency> dependencies) =>
        dependencies.Where(dependency => !IsExcluded(dependency)).ToArray();
}
