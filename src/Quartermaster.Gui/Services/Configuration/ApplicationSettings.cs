using System.Text.Json;
using Quartermaster.Library.Storage;

namespace Quartermaster.Gui.Services;

public enum RepatchMode { Ask, Automatic, Never }
public sealed record ApplicationSettings(string? GameDataDirectory = null, RepatchMode Repatch = RepatchMode.Ask,
    ThemePreset Theme = ThemePreset.Dark, string AccentColor = ThemeManager.DefaultAccent,
    bool AllowAutomaticUpdate = false, string? SkippedAppUpdateVersion = null, bool OnboardingCompleted = false, string? Language = null);

public sealed class SettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "settings.json");
    public string LocalizationDirectory => Path.Combine(directory, "localization");
    public async Task<ApplicationSettings> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        try
        {
            var settings = await JsonSerializer.DeserializeAsync<ApplicationSettings>(file, cancellationToken: ct) ?? new();
            if (!Enum.IsDefined(settings.Repatch)) throw new InvalidDataException(Localizer.Text("Invalid repatch setting."));
            ThemeManager.Validate(settings.Theme, settings.AccentColor);
            return settings;
        }
        catch (JsonException ex) { throw new InvalidDataException(Localizer.Text("Application settings are invalid."), ex); }
    }
    public async Task SaveAsync(ApplicationSettings settings, CancellationToken ct)
    {
        ThemeManager.Validate(settings.Theme, settings.AccentColor);
        Directory.CreateDirectory(directory);
        var temporary = TemporaryStorage.PathFor(directory, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, settings, cancellationToken: ct);
                await file.FlushAsync(ct); file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
