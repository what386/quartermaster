using System.Globalization;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Interop.Arsenal;

/// <summary>Reads Arsenal's shared library and per-profile state without changing its files.</summary>
public static class ArsenalReader
{
    public static string DefaultDirectory => Path.Combine(OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "hd2arsenal");

    public static async Task<ArsenalImportPlan> ReadAsync(string directory, IReadOnlyCollection<string>? profileKeys = null, CancellationToken ct = default)
    {
        directory = Path.GetFullPath(directory);
        await using var stream = File.OpenRead(Path.Combine(directory, "hd2a_data.json"));
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("modsList", out var lists) || lists.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The folder does not contain an Arsenal profile library.");
        var keys = root.TryGetProperty("profileOrder", out var order) && order.ValueKind == JsonValueKind.Array
            ? order.EnumerateArray().Select(value => value.GetString() ?? "").ToList() : [];
        keys.AddRange(lists.EnumerateObject().Select(property => property.Name));
        var available = keys.Distinct(StringComparer.Ordinal).Where(key => lists.TryGetProperty(key, out _)).ToArray();
        if (profileKeys is not null && profileKeys.Any(key => !available.Contains(key, StringComparer.Ordinal)))
            throw new InvalidDataException("An Arsenal profile was not found.");
        var records = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (root.TryGetProperty("modsLibrary", out var library) && library.ValueKind == JsonValueKind.Array)
            foreach (var mod in library.EnumerateArray())
            {
                var id = Required(mod, "uuid");
                if (!records.TryAdd(id, mod)) throw new InvalidDataException($"Duplicate Arsenal library mod: {id}");
            }
        var priority = Bool(root, "setTopPriority", false) ? PriorityDirection.FirstWins : PriorityDirection.LastWins;
        var profiles = new List<ArsenalProfile>();
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in available.Where(key => profileKeys is null || profileKeys.Contains(key)))
        {
            ct.ThrowIfCancellationRequested();
            var profile = lists.GetProperty(key);
            if (!profile.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Invalid Arsenal profile: {key}");
            var rows = new List<ArsenalRow>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mod in mods.EnumerateArray())
            {
                var id = Required(mod, "uuid");
                if (Text(mod, "type") == "separator")
                { rows.Add(new(id, Required(mod, "label"), false, null)); continue; }
                if (!seen.Add(id)) throw new InvalidDataException($"Duplicate mod in Arsenal profile: {key}");
                // Older Arsenal profiles contain complete mod records; newer ones can be UUID-only.
                if (!records.ContainsKey(id) && Text(mod, "path") is not null) records.Add(id, mod);
                if (!records.TryGetValue(id, out var metadata)) throw new InvalidDataException($"Arsenal profile references a missing library mod: {id}");
                var options = mod.TryGetProperty("optionsConfig", out var config) ? ReadOptions(config) :
                    mod.TryGetProperty("options", out var oldOptions) ? ReadOptions(oldOptions) :
                    metadata.TryGetProperty("options", out var defaults) ? ReadOptions(defaults) : null;
                rows.Add(new(id, null, Bool(mod, "enabled", true), options));
                required.Add(id);
            }
            profiles.Add(new(key, Text(profile, "label") ?? key, priority, rows));
        }
        if (profiles.Count == 0) throw new InvalidDataException("No Arsenal profiles were selected.");
        var imports = new List<ArsenalMod>();
        foreach (var id in required)
        {
            ct.ThrowIfCancellationRequested();
            var mod = records[id];
            var path = ResolveModDirectory(directory, Required(mod, "path"));
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Arsenal mod files were not found: {Text(mod, "label") ?? id} ({path})");
            var addedAt = DateTimeOffset.TryParse(Text(mod, "addedAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var added) ? added : (DateTimeOffset?)null;
            imports.Add(new(id, Text(mod, "label") ?? Path.GetFileName(path), path, Text(mod, "description") ?? "", addedAt, ReadSource(mod)));
        }
        return new(directory, Text(root, "selectedProfile"), imports, profiles);
    }

    private static string ResolveModDirectory(string directory, string original)
    {
        // Portable copies retain the old absolute paths. Prefer their local mods directory.
        var leaf = original.TrimEnd('/', '\\').Split('/', '\\').Last();
        if (leaf is "" or "." or "..") throw new InvalidDataException("Invalid Arsenal mod directory.");
        var local = Path.Combine(directory, "mods", leaf);
        if (Directory.Exists(local)) return local;
        if (Path.IsPathFullyQualified(original)) return Path.GetFullPath(original);
        if (original.Replace('\\', '/').Split('/').Any(part => part == "..")) throw new InvalidDataException("Unsafe relative Arsenal mod directory.");
        return Path.GetFullPath(Path.Combine(directory, original.Replace('\\', Path.DirectorySeparatorChar)));
    }
    private static IReadOnlyList<ArsenalOptionSelection>? ReadOptions(JsonElement options)
    {
        if (options.ValueKind == JsonValueKind.Null) return null;
        if (options.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid Arsenal option selections.");
        return options.EnumerateArray().Select(option => new ArsenalOptionSelection(Required(option, "name"), Bool(option, "enabled", true),
            option.TryGetProperty("suboptions", out var choices) && choices.ValueKind == JsonValueKind.Array
                ? choices.EnumerateArray().Select(choice => new ArsenalChoiceSelection(Required(choice, "name"), Bool(choice, "enabled", false))).ToArray() : [])).ToArray();
    }
    private static SourceReference? ReadSource(JsonElement mod)
    {
        if (!mod.TryGetProperty("nexusData", out var nexus) || nexus.ValueKind != JsonValueKind.Object) return null;
        var id = Text(nexus, "modId") ?? Text(nexus, "mod_id");
        if (!long.TryParse(id, out var number) || number <= 0) return null;
        var game = Text(nexus, "gameDomain") ?? Text(nexus, "game_domain_name");
        if (game is not null && game != "helldivers2") return null;
        var file = Text(nexus, "fileId") ?? Text(nexus, "file_id");
        if (file is not null && (!long.TryParse(file, out var fileId) || fileId <= 0)) file = null;
        return new("nexusmods", id!, file, Text(nexus, "version"));
    }
    private static string Required(JsonElement value, string name) => Text(value, name) is { Length: > 0 } text ? text : throw new InvalidDataException($"Arsenal data is missing {name}.");
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property.ValueKind switch
    { JsonValueKind.String => property.GetString(), JsonValueKind.Number => property.GetRawText(), _ => null } : null;
    private static bool Bool(JsonElement value, string name, bool fallback) => value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : fallback;
}
