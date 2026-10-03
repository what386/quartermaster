using Quartermaster.Core.Patching;
using global::System.Text.RegularExpressions;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library;

public static partial class SteamGameDiscovery
{
    public static string? ResolveDataDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (GameArchives.IsValidDataDirectory(path)) return ManagedPaths.CanonicalDirectory(path);
        var data = Path.Combine(path, "data");
        return GameArchives.IsValidDataDirectory(data) ? ManagedPaths.CanonicalDirectory(data) : null;
    }
    public static IReadOnlyList<string> FindInstallations(IEnumerable<string>? steamRoots = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var libraries = new HashSet<string>(comparer);
        foreach (var root in steamRoots ?? DefaultRoots())
        {
            if (!Directory.Exists(root)) continue;
            libraries.Add(Path.GetFullPath(root));
            var config = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(config)) continue;
            foreach (Match match in LibraryPath().Matches(File.ReadAllText(config)))
                libraries.Add(Path.GetFullPath(match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"")));
        }
        return libraries.Select(l => ResolveDataDirectory(Path.Combine(l, "steamapps", "common", "Helldivers 2")))
            .OfType<string>().Distinct(comparer).Order(comparer).ToArray();
    }
    private static IEnumerable<string> DefaultRoots()
    {
        var userDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows()) yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        else if (OperatingSystem.IsMacOS()) yield return Path.Combine(userDirectory, "Library", "Application Support", "Steam");
        else { yield return Path.Combine(userDirectory, ".steam", "steam"); yield return Path.Combine(userDirectory, ".local", "share", "Steam"); }
    }
    [GeneratedRegex("\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPath();
}
