using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Quartermaster.FileMerger;

public enum ConfigTableKind { Rows, Keyed, Passive, Recharge }
public sealed record ConfigTableVariant(int RecordSize, int IdOffset, IReadOnlyDictionary<int, string> Fields);
public sealed record ConfigTableDefinition(uint Type, string Name, ConfigTableKind Kind, int MaxRecords,
    IReadOnlyList<ConfigTableVariant> Variants);

/// <summary>Immutable, validated definitions for the built-in table handlers. No executable rules or external includes.</summary>
public sealed class ConfigTableRegistry
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly Lazy<ConfigTableRegistry> Bundled = new(() =>
    {
        using var stream = typeof(ConfigTableRegistry).Assembly.GetManifestResourceStream("Quartermaster.FileMerger.Data.config-tables.json")
            ?? throw new InvalidDataException("The bundled config table registry is missing.");
        return Load(stream);
    });
    private readonly FrozenDictionary<uint, ConfigTableDefinition> byType;
    public static ConfigTableRegistry Default => Bundled.Value;
    public IReadOnlyList<ConfigTableDefinition> Tables { get; }
    /// <summary>Stable hash of active layout/limit rules. Display names, labels and JSON formatting do not affect it.</summary>
    public string Fingerprint { get; }
    public bool TryGet(uint type, out ConfigTableDefinition definition) => byType.TryGetValue(type, out definition!);

    private ConfigTableRegistry(IReadOnlyList<ConfigTableDefinition> tables)
    {
        Tables = tables; byType = tables.ToFrozenDictionary(table => table.Type);
        var rules = tables.OrderBy(table => table.Type).Select(table => new
        {
            table.Type,
            table.Kind,
            table.MaxRecords,
            Variants = table.Variants.OrderBy(variant => variant.RecordSize).ThenBy(variant => variant.IdOffset)
                .Select(variant => new { variant.RecordSize, variant.IdOffset })
        });
        Fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(rules)));
    }

    /// <summary>Loads all definitions or fails. The input stream remains open. Custom registries are scoped to the caller.</summary>
    public static ConfigTableRegistry Load(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var buffer = new MemoryStream(); var chunk = new byte[4096];
            int count;
            while ((count = json.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + count > MaxBytes) throw Invalid("file exceeds 1 MiB");
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            Properties(root, "root", "$schema", "schemaVersion", "tables");
            if (root.TryGetProperty("$schema", out _)) _ = Text(root, "$schema", "root");
            if (Integer(root, "schemaVersion", "root") != 1) throw Invalid("unsupported schemaVersion; expected 1");
            var source = Required(root, "tables", "root");
            if (source.ValueKind != JsonValueKind.Array || source.GetArrayLength() is < 1 or > 1024) throw Invalid("tables must contain 1–1024 entries");
            var tables = new List<ConfigTableDefinition>(); var types = new HashSet<uint>();
            foreach (var item in source.EnumerateArray())
            {
                Properties(item, "table", "type", "name", "layout", "recordSize", "idOffset", "fields", "variants", "maxRecords");
                var rawType = Text(item, "type", "table");
                if (rawType.Length != 10 || !rawType.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                    !uint.TryParse(rawType.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var type) || type == 0)
                    throw Invalid($"table type '{rawType}' must be a nonzero 0x-prefixed eight-digit hexadecimal ID");
                var context = $"table {rawType}";
                if (!types.Add(type)) throw Invalid($"duplicate type {rawType}");
                var name = Text(item, "name", context);
                var layout = Text(item, "layout", context);
                var kind = layout switch
                {
                    "id-rows" => ConfigTableKind.Rows,
                    "entity-keyed" => ConfigTableKind.Keyed,
                    "armor-passive" => ConfigTableKind.Passive,
                    "recharge" => ConfigTableKind.Recharge,
                    _ => throw Invalid($"{context}: unknown layout '{layout}'; new structures require a C# handler")
                };
                var ceiling = kind == ConfigTableKind.Passive ? 4096 : 250_000;
                var limit = item.TryGetProperty("maxRecords", out _) ? Integer(item, "maxRecords", context) : ceiling;
                if (limit < 1 || limit > ceiling) throw Invalid($"{context}: maxRecords must be between 1 and {ceiling}");
                var variants = new List<ConfigTableVariant>();
                if (kind is ConfigTableKind.Passive or ConfigTableKind.Recharge)
                {
                    if (new[] { "recordSize", "idOffset", "fields", "variants" }.Any(key => item.TryGetProperty(key, out _)))
                        throw Invalid($"{context}: {layout} structure rules are owned by its C# handler");
                }
                else if (item.TryGetProperty("variants", out var alternatives))
                {
                    if (new[] { "recordSize", "idOffset", "fields" }.Any(key => item.TryGetProperty(key, out _)))
                        throw Invalid($"{context}: use either variants or a single recordSize/idOffset/fields definition");
                    if (alternatives.ValueKind != JsonValueKind.Array || alternatives.GetArrayLength() is < 1 or > 8)
                        throw Invalid($"{context}: variants must contain 1–8 layouts");
                    foreach (var variant in alternatives.EnumerateArray())
                    {
                        Properties(variant, context + " variant", "recordSize", "idOffset", "fields");
                        variants.Add(Variant(variant, kind, context));
                    }
                }
                else variants.Add(Variant(item, kind, context));
                if (variants.Select(variant => (variant.RecordSize, variant.IdOffset)).Distinct().Count() != variants.Count)
                    throw Invalid($"{context}: duplicate layout variants");
                tables.Add(new(type, name, kind, limit, variants.AsReadOnly()));
            }
            return new(tables.AsReadOnly());
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid config table registry JSON: " + ex.Message, ex); }
    }

    private static ConfigTableVariant Variant(JsonElement value, ConfigTableKind kind, string context)
    {
        var size = Integer(value, "recordSize", context);
        if (size is < 4 or > 1024 * 1024 || size % 4 != 0) throw Invalid($"{context}: recordSize must be a multiple of 4 between 4 and 1048576");
        var offset = value.TryGetProperty("idOffset", out _) ? Integer(value, "idOffset", context) : 0;
        if (offset < 0 || offset % 4 != 0 || offset > size - 4 || kind == ConfigTableKind.Keyed && offset != 0)
            throw Invalid($"{context}: idOffset must address a complete aligned u32 ID; entity-keyed IDs live in the index and require offset 0");
        var fields = new Dictionary<int, string>();
        if (value.TryGetProperty("fields", out var names))
        {
            if (names.ValueKind != JsonValueKind.Object || names.EnumerateObject().Count() > 256) throw Invalid($"{context}: fields must be an object with at most 256 labels");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in names.EnumerateObject())
            {
                if (!seen.Add(field.Name) || !int.TryParse(field.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var position) ||
                    position < 0 || position % 4 != 0 || position > size - 4 || !fields.TryAdd(position, Label(field.Value, context + " field")))
                    throw Invalid($"{context}: duplicate or invalid field offset '{field.Name}'");
            }
        }
        return new(size, offset, fields.ToFrozenDictionary());
    }

    private static void Properties(JsonElement value, string context, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{context} must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Invalid($"{context}: unknown or duplicate property '{property.Name}'");
    }
    private static JsonElement Required(JsonElement value, string property, string context) =>
        value.TryGetProperty(property, out var result) ? result : throw Invalid($"{context}: missing {property}");
    private static int Integer(JsonElement value, string property, string context)
    {
        var number = Required(value, property, context);
        return number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var result)
            ? result : throw Invalid($"{context}: {property} must be an integer");
    }
    private static string Text(JsonElement value, string property, string context) => Label(Required(value, property, context), context + " " + property);
    private static string Label(JsonElement value, string context) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 128 } text && !string.IsNullOrWhiteSpace(text)
            ? text.Trim() : throw Invalid($"{context} must be a nonempty string of at most 128 characters");
    private static InvalidDataException Invalid(string message) => new("Invalid config table registry: " + message + ".");
}
