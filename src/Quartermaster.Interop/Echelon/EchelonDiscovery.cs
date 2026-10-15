using System.Text.Json;
using System.Text.RegularExpressions;

namespace Quartermaster.Interop.Echelon;

/// <summary>Checks known data locations and Wine/Proton prefixes without scanning the whole disk.</summary>
public static partial class EchelonDiscovery
{
    public static IReadOnlyList<string> FindDirectories(string? homeDirectory = null, string? appDataDirectory = null,
        IEnumerable<string>? winePrefixes = null, IEnumerable<string>? steamRoots = null, CancellationToken ct = default)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = appDataDirectory ?? (OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : Environment.GetEnvironmentVariable("APPDATA"));
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var candidates = new HashSet<string>(comparer) { Path.Combine(home, ".config", "Echelon") };
        if (!string.IsNullOrWhiteSpace(appData)) candidates.Add(Path.Combine(appData, "Echelon"));
        var prefixes = new HashSet<string>(winePrefixes ?? DefaultPrefixes(home), comparer);
        var libraries = new HashSet<string>(comparer);
        foreach (var root in steamRoots ?? DefaultSteamRoots(home))
        {
            ct.ThrowIfCancellationRequested();
            libraries.Add(root);
            try
            {
                var config = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(config)) continue;
                foreach (Match match in LibraryPath().Matches(File.ReadAllText(config)))
                {
                    var library = match.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
                    if (Path.IsPathFullyQualified(library)) libraries.Add(library);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        foreach (var library in libraries)
            foreach (var app in Directories(Path.Combine(library, "steamapps", "compatdata")))
            {
                ct.ThrowIfCancellationRequested();
                prefixes.Add(Path.Combine(app, "pfx"));
            }
        foreach (var prefix in prefixes)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var user in Directories(Path.Combine(prefix, "drive_c", "users")))
                candidates.Add(Path.Combine(user, "AppData", "Roaming", "Echelon"));
        }
        var found = new HashSet<string>(comparer);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(candidate)) continue;
                var path = CanonicalDirectory(candidate);
                if (IsDataDirectory(path)) found.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
        }
        return found.Order(comparer).ToArray();
    }

    private static bool IsDataDirectory(string directory)
    {
        using var stream = new FileStream(Path.Combine(directory, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("version", out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 2 &&
            root.TryGetProperty("library", out var library) && library.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array;
    }
    private static IEnumerable<string> DefaultPrefixes(string home)
    {
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } prefix) yield return prefix;
        yield return Path.Combine(home, ".wine");
        foreach (var bottles in new[] { Path.Combine(home, ".local", "share", "bottles", "bottles"),
            Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles") })
            foreach (var bottle in Directories(bottles)) yield return bottle;
    }
    private static IEnumerable<string> DefaultSteamRoots(string home)
    {
        yield return Path.Combine(home, ".steam", "steam");
        yield return Path.Combine(home, ".local", "share", "Steam");
        yield return Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam");
    }
    private static string[] Directories(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }
    private static string CanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var info = new DirectoryInfo(Path.Combine(current, part));
            current = (info.Attributes & FileAttributes.ReparsePoint) != 0 ? info.ResolveLinkTarget(true)!.FullName : info.FullName;
        }
        return current;
    }
    [GeneratedRegex("\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPath();
}
