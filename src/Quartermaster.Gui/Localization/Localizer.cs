using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Quartermaster.Gui.Localization;

public sealed record LanguageOption(string Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>English source text is the translation key; user content is never translated.</summary>
public sealed class Localizer
{
    public static Localizer Current { get; } = new();
    private readonly Dictionary<string, Catalog> catalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeakReference<ILocalizationListener>> bindings = [];
    private readonly Dictionary<string, IReadOnlyList<string>> choices = [];
    private readonly CultureInfo systemCulture = CultureInfo.CurrentUICulture;
    private readonly Catalog english;
    private Catalog selected;
    private IReadOnlyList<Catalog> fallbacks;
    public string Language => selected.Language;
    public CultureInfo Culture { get; private set; }
    public IReadOnlyList<LanguageOption> Languages =>
        new[] { new LanguageOption("", Get("System default")) }.Concat(catalogs.Values
            .OrderBy(catalog => catalog.Name, StringComparer.OrdinalIgnoreCase)
            .Select(catalog => new LanguageOption(catalog.Language, catalog.Name))).ToArray();

    public Localizer()
    {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream("Quartermaster.Gui.Localization.Catalogs.en.json")
            ?? throw new InvalidOperationException("The English localization catalog is missing.");
        english = ReadCatalog(stream);
        catalogs.Add(english.Language, english);
        selected = english;
        fallbacks = [english];
        Culture = CultureInfo.GetCultureInfo(english.Language);
    }

    /// <summary>Load optional UTF-8 catalogs, ignoring invalid files without losing English fallback.</summary>
    public IReadOnlyList<string> LoadDirectory(string directory)
    {
        var errors = new List<string>();
        if (!Directory.Exists(directory)) return errors;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var catalog = ReadCatalog(stream);
                if (catalog.Language.Equals("en", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var (key, value) in catalog.Strings)
                {
                    if (!english.Strings.ContainsKey(key)) continue;
                    if (!FormatArguments(key).SetEquals(FormatArguments(value)))
                        throw new InvalidDataException($"Translation placeholders do not match: {key}");
                }
                catalogs[catalog.Language] = catalog;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
            { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return errors;
    }

    public void SetLanguage(string? language)
    {
        var culture = string.IsNullOrWhiteSpace(language) ? systemCulture : TryCulture(language) ?? systemCulture;
        var matches = new List<Catalog>();
        for (var parent = culture; parent.Name != ""; parent = parent.Parent)
            if (catalogs.TryGetValue(parent.Name, out var catalog)) matches.Add(catalog);
        if (!matches.Contains(english)) matches.Add(english);
        fallbacks = matches;
        selected = matches[0];
        Culture = culture;
        choices.Clear();
        if (ReferenceEquals(this, Current))
        {
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        bindings.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (var reference in bindings.ToArray())
            if (reference.TryGetTarget(out var binding)) binding.RefreshLocalization();
    }
    public string Get(string source)
    {
        foreach (var catalog in fallbacks)
            if (catalog.Strings.TryGetValue(source, out var translation)) return translation;
        return source;
    }
    public string Format(string source, params object?[] arguments)
    {
        var translation = Get(source);
        try { return string.Format(Culture, translation, arguments); }
        catch (FormatException) when (translation != source)
        { return string.Format(Culture, english.Strings.GetValueOrDefault(source) ?? source, arguments); }
    }
    public string SearchTerms(string source) => source + " " + string.Join(" ", english.Strings.Keys
        .Where(key => key.Length > 2 && !key.Contains('{') && source.Contains(key, StringComparison.OrdinalIgnoreCase)).Select(Get));
    public IReadOnlyList<string> Choices(params string[] sources)
    {
        var key = string.Join('\0', sources);
        if (!choices.TryGetValue(key, out var translated)) choices[key] = translated = Array.AsReadOnly(sources.Select(Get).ToArray());
        return translated;
    }
    internal void Watch(ILocalizationListener binding)
    {
        if (bindings.Count % 64 == 0) bindings.RemoveAll(reference => !reference.TryGetTarget(out _));
        bindings.Add(new(binding));
    }
    public static string Text(string source) => Current.Get(source);
    public static string Interpolate(FormattableString source) => Current.Format(source.Format, source.GetArguments());

    private static CultureInfo? TryCulture(string language)
    {
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return null; }
    }
    private static Catalog ReadCatalog(Stream stream)
    {
        var catalog = JsonSerializer.Deserialize<Catalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty localization catalog.");
        if (string.IsNullOrWhiteSpace(catalog.Language) || TryCulture(catalog.Language) is null ||
            string.IsNullOrWhiteSpace(catalog.Name) || catalog.Strings is null || catalog.Strings.Any(pair => pair.Value is null))
            throw new InvalidDataException("A catalog needs a valid language, name, and string table.");
        return catalog;
    }
    private static HashSet<int> FormatArguments(string format)
    {
        // CompositeFormat also rejects malformed braces and invalid format items.
        _ = CompositeFormat.Parse(format);
        var arguments = new HashSet<int>();
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] != '{') continue;
            if (i + 1 < format.Length && format[i + 1] == '{') { i++; continue; }
            var end = i + 1;
            while (end < format.Length && char.IsAsciiDigit(format[end])) end++;
            arguments.Add(int.Parse(format.AsSpan(i + 1, end - i - 1), CultureInfo.InvariantCulture));
        }
        return arguments;
    }
    private sealed record Catalog(string Language, string Name, Dictionary<string, string> Strings);
}

internal interface ILocalizationListener { void RefreshLocalization(); }

public sealed class LocalizedText : INotifyPropertyChanged, ILocalizationListener
{
    private readonly string source;
    public LocalizedText(string source) { this.source = source; Localizer.Current.Watch(this); }
    public string Value => Localizer.Text(source);
    public event PropertyChangedEventHandler? PropertyChanged;
    void ILocalizationListener.RefreshLocalization() => PropertyChanged?.Invoke(this, new(nameof(Value)));
}
