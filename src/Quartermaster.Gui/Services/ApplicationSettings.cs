using System.Text.Json;

namespace Quartermaster.Gui.Services;

public sealed record ApplicationSettings(string? GameDataDirectory = null);

public sealed class SettingsStore(string directory)
{
    private readonly string path = Path.Combine(directory, "settings.json");
    public async Task<ApplicationSettings> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(path)) return new();
        await using var file = File.OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync<ApplicationSettings>(file, cancellationToken: ct) ?? new(); }
        catch (JsonException ex) { throw new InvalidDataException("Application settings are invalid.", ex); }
    }
    public async Task SaveAsync(ApplicationSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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
