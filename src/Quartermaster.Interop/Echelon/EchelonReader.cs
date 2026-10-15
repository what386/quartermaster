using System.Globalization;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Interop.Echelon;

/// <summary>Reads Echelon 0.36's version-2 state and original stored patch sets, never deployed game files.</summary>
public static class EchelonReader
{
    public static string DefaultDirectory => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : Environment.GetEnvironmentVariable("APPDATA") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "Echelon");

    public static async Task<EchelonImportPlan> ReadAsync(string directory, IReadOnlyCollection<string>? profileKeys = null, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        using var document = await ReadJson(Path.Combine(directory, "state.json"), ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var schema) || schema != 2)
            throw new InvalidDataException("Unsupported Echelon state version. Expected version 2.");
        var library = Object(root, "library");
        var profiles = Array(root, "profiles");
        var modRoot = Path.Combine(directory, "mods");
        string? customRoot = null;
        var locationsPath = Path.Combine(directory, "locations.json");
        if (File.Exists(locationsPath))
        {
            using var locations = await ReadJson(locationsPath, ct).ConfigureAwait(false);
            customRoot = Text(locations.RootElement, "mods");
        }
        var mods = new List<EchelonMod>();
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in library.EnumerateObject())
        {
            ct.ThrowIfCancellationRequested();
            var value = property.Value;
            var id = Required(value, "id");
            if (id != property.Name || !metadata.TryAdd(id, value)) throw new InvalidDataException("Invalid or duplicate Echelon library mod ID.");
            var storage = Text(value, "store") ?? id;
            var local = Path.Combine(modRoot, storage.Replace('\\', '/').TrimEnd('/').Split('/').Last());
            var storageRoot = Directory.Exists(local) || string.IsNullOrWhiteSpace(customRoot) ? modRoot : ResolveDirectory(directory, customRoot);
            var path = ResolveDirectory(storageRoot, storage);
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Echelon mod files were not found: {Required(value, "name")} ({path})");
            var sets = Array(value, "sets").EnumerateArray().Select(set =>
            {
                var files = Object(set, "files").EnumerateObject().Select(file => file.Value.GetString() ?? "").ToArray();
                if (files.Length == 0) throw new InvalidDataException("An Echelon patch set contains no files.");
                foreach (var file in files) ResolveFile(path, file);
                return (IReadOnlyList<string>)files;
            }).ToArray();
            if (sets.Length == 0) throw new InvalidDataException("An Echelon mod contains no patch sets.");
            var options = OptionalArray(value, "options").Select(option => new EchelonOption(Required(option, "name"), Text(option, "description") ?? "",
                Indices(option, "include", sets.Length), OptionalArray(option, "subs").Select(choice => new EchelonChoice(
                    Required(choice, "name"), Text(choice, "description") ?? "", Indices(choice, "include", sets.Length)) { Image = ReadImage(path, Text(choice, "image")) }).ToArray())
                { Image = ReadImage(path, Text(option, "image")) }).ToArray();
            // Echelon excludes unreferenced sets when options exist; Quartermaster treats
            // them as always-on content. Reject inconsistent definitions rather than
            // silently enabling files the source profile would never deploy.
            var assigned = options.SelectMany(option => option.Sets.Concat(option.Choices.SelectMany(choice => choice.Sets))).ToHashSet();
            if (options.Length > 0 && assigned.Count != sets.Length)
                throw new InvalidDataException("Echelon mod has stored patch sets outside its option definitions.");
            var addedAt = value.TryGetProperty("added_at", out var time) && time.TryGetDouble(out var seconds) && double.IsFinite(seconds)
                ? DateTimeOffset.UnixEpoch.AddSeconds(seconds) : DateTimeOffset.TryParse(Text(value, "added"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : (DateTimeOffset?)null;
            var release = Text(value, "version");
            mods.Add(new(id, Required(value, "name"), path, Text(value, "description") ?? "", addedAt, release, ReadSource(value, release),
                ModTags.Normalize(OptionalArray(value, "tags").Select(tag => tag.GetString() ?? "")), sets, options)
            {
                ManifestId = Guid.TryParse(Text(value, "guid"), out var manifestId) ? manifestId.ToString() : null,
                PageLink = ReadPage(Text(value, "web")),
                Image = ReadImage(Path.Combine(directory, "images"), Text(value, "image"))
            });
        }
        var byId = mods.ToDictionary(mod => mod.Id, StringComparer.Ordinal);
        var importedProfiles = new List<EchelonProfile>();
        var seenProfiles = new HashSet<string>(StringComparer.Ordinal);
        var priority = root.TryGetProperty("settings", out var settings) && Text(settings, "priority") == "first" ? PriorityDirection.FirstWins : PriorityDirection.LastWins;
        foreach (var profile in profiles.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var id = Required(profile, "id");
            if (!seenProfiles.Add(id)) throw new InvalidDataException("Duplicate Echelon profile ID.");
            if (profileKeys is not null && !profileKeys.Contains(id)) continue;
            var rows = new List<EchelonRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in Array(profile, "entries").EnumerateArray())
            {
                var type = Required(entry, "type");
                var modId = Required(entry, "id");
                if (type == "sep")
                {
                    rows.Add(new(modId, Required(entry, "title"), false, [])
                    {
                        BackgroundColor = ProfileAppearance.NormalizeColor(Text(entry, "bg")),
                        TextColor = ProfileAppearance.NormalizeColor(Text(entry, "fg"))
                    });
                    continue;
                }
                if (type != "mod") throw new InvalidDataException($"Unsupported Echelon profile entry: {type}");
                if (!seen.Add(modId) || !byId.TryGetValue(modId, out var mod)) throw new InvalidDataException($"Duplicate or missing Echelon profile mod: {modId}");
                var saved = entry.TryGetProperty("options", out var configuration) && configuration.ValueKind == JsonValueKind.Object && configuration.EnumerateObject().Any()
                    ? configuration : metadata[modId].TryGetProperty("option_defaults", out var defaults) ? defaults : default;
                var on = OptionalArray(saved, "on");
                var selected = OptionalArray(saved, "sel");
                var choices = mod.Options.Select((option, index) => new EchelonOptionSelection(
                    index >= on.Length || Boolean(on[index]), index < selected.Length && selected[index].TryGetInt32(out var choice)
                        ? Math.Clamp(choice, 0, Math.Max(0, option.Choices.Count - 1)) : 0)).ToArray();
                rows.Add(new(modId, null, Bool(entry, "enabled", true), choices));
            }
            string? thumbnail = null;
            if (Text(profile, "icon") is { Length: > 0 } icon)
            {
                var image = ResolveFile(Path.Combine(directory, "icons"), icon);
                if (File.Exists(image)) thumbnail = await ProfileAppearance.ReadThumbnailAsync(image, ct).ConfigureAwait(false);
            }
            importedProfiles.Add(new(id, Required(profile, "name"), priority, rows) { Thumbnail = thumbnail });
        }
        if (profileKeys is not null && profileKeys.Any(key => !seenProfiles.Contains(key))) throw new InvalidDataException("An Echelon profile was not found.");
        if (importedProfiles.Count == 0) throw new InvalidDataException("No Echelon profiles were selected.");
        return new(directory, Text(root, "active_profile"), mods, importedProfiles);
    }

    private static string? ReadPage(string? page)
    {
        try { return ModLinks.ValidatePage(page); }
        catch (ArgumentException) { return null; }
    }
    private static string? ReadImage(string directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var path = ResolveFile(directory, relative);
        return File.Exists(path) ? path : null;
    }

    internal static string ResolveFile(string directory, string relative)
    {
        var normalized = relative.Replace('\\', '/');
        var parts = normalized.Split('/');
        if (string.IsNullOrWhiteSpace(normalized) || parts.Any(part => part is "" or "." or ".." || part.Contains(':')) || Path.IsPathRooted(normalized))
            throw new InvalidDataException("Unsafe Echelon file path.");
        var path = directory;
        for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent) CheckLink(parent.FullName);
        foreach (var part in parts) { path = Path.Combine(path, part); CheckLink(path); }
        return path;
    }
    private static void CheckLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Echelon mod contains a symbolic link.");
    }
    private static string ResolveDirectory(string directory, string original)
    {
        var normalized = original.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part == "..")) throw new InvalidDataException("Unsafe Echelon storage path.");
        var leaf = normalized.TrimEnd('/').Split('/').Last();
        if (leaf is "" or ".") throw new InvalidDataException("Invalid Echelon storage directory.");
        var local = Path.Combine(directory, leaf);
        if (Directory.Exists(local)) return local;
        if (Path.IsPathFullyQualified(original)) return Path.GetFullPath(original);
        // Map original C: paths when reading directly from a Wine prefix on Linux.
        if (normalized.StartsWith("C:/", StringComparison.OrdinalIgnoreCase))
        {
            for (var parent = new DirectoryInfo(directory); parent is not null; parent = parent.Parent)
                if (parent.Name == "drive_c") return Path.Combine(parent.FullName, normalized[3..]);
            throw new DirectoryNotFoundException($"Echelon storage is unavailable: {original}. Copy its mods folder alongside state.json.");
        }
        return ResolveFile(directory, normalized);
    }
    private static SourceReference? ReadSource(JsonElement mod, string? version)
    {
        var id = mod.TryGetProperty("nexus", out var nexus) ? Text(nexus, "mod_id") : null;
        if (Text(mod, "web") is { } web && Uri.TryCreate(web, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host is "www.nexusmods.com" or "nexusmods.com" && uri.UserInfo.Length == 0 && uri.IsDefaultPort)
        {
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length == 3 && parts[0] == "helldivers2" && parts[1] == "mods") id = parts[2];
        }
        return long.TryParse(id, out var number) && number > 0 ? new("nexusmods", number.ToString(CultureInfo.InvariantCulture), InstalledVersion: version) : null;
    }
    private static int[] Indices(JsonElement value, string key, int count) => OptionalArray(value, key).Select(item =>
        item.TryGetInt32(out var index) && index >= 0 && index < count ? index : throw new InvalidDataException("Invalid Echelon option patch-set index.")).ToArray();
    private static async Task<JsonDocument> ReadJson(string path, CancellationToken ct)
    { await using var stream = File.OpenRead(path); return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false); }
    private static JsonElement Object(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Object ? item : throw new InvalidDataException($"Echelon data is missing {key}.");
    private static JsonElement Array(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) && item.ValueKind == JsonValueKind.Array ? item : throw new InvalidDataException($"Echelon data is missing {key}.");
    private static JsonElement[] OptionalArray(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var item) || item.ValueKind == JsonValueKind.Null) return [];
        if (item.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"Invalid Echelon {key} list.");
        return item.EnumerateArray().ToArray();
    }
    private static string Required(JsonElement value, string key) => Text(value, key) is { Length: > 0 } text ? text : throw new InvalidDataException($"Echelon data is missing {key}.");
    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? item.ValueKind switch { JsonValueKind.String => item.GetString(), JsonValueKind.Number => item.GetRawText(), _ => null } : null;
    private static bool Bool(JsonElement value, string key, bool fallback) => value.TryGetProperty(key, out var item) ? Boolean(item) : fallback;
    private static bool Boolean(JsonElement item) => item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw new InvalidDataException("Invalid Echelon enable state.");
}
