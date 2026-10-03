using Quartermaster.Core.Patching;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using global::System.Text.Json;
using global::System.Text.Json.Serialization;

namespace Quartermaster.Library.Storage;

public sealed class JsonLibraryStore : ILibraryStore
{
    public string Root { get; }
    public JsonLibraryStore(string root) => Root = Path.GetFullPath(root);
    private sealed record LibraryDocument(int SchemaVersion, IReadOnlyList<Mod> Mods, IReadOnlyList<UpdateCheck> UpdateChecks);
    private sealed record ProfilesDocument(int SchemaVersion, IReadOnlyList<Profile> Profiles, Guid? ActiveProfileId);
    public async Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        var libraryPath = ManagedPaths.Resolve(Root, "library.json");
        var profilesPath = ManagedPaths.Resolve(Root, "profiles.json");
        LibraryState state;
        if (!File.Exists(libraryPath) && !File.Exists(profilesPath))
        {
            var legacy = ManagedPaths.Resolve(Root, "state.json");
            if (!File.Exists(legacy)) return LibraryState.Empty;
            state = await JsonFiles.ReadAsync<LibraryState>(legacy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            if (!File.Exists(libraryPath) || !File.Exists(profilesPath)) throw new InvalidDataException("Library or profile metadata is missing.");
            var library = await JsonFiles.ReadAsync<LibraryDocument>(libraryPath, cancellationToken).ConfigureAwait(false);
            var profiles = await JsonFiles.ReadAsync<ProfilesDocument>(profilesPath, cancellationToken).ConfigureAwait(false);
            if (profiles.SchemaVersion != library.SchemaVersion) throw new InvalidDataException("Library/profile schema mismatch.");
            state = new(library.SchemaVersion, library.Mods, profiles.Profiles, profiles.ActiveProfileId) { UpdateChecks = library.UpdateChecks };
        }
        try { StateValidation.Validate(state); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new InvalidDataException("Invalid library state.", ex); }
        return state;
    }
    public async Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default)
    {
        StateValidation.Validate(state);
        var patches = ManagedPaths.Resolve(Root, "patches.json");
        if (!File.Exists(patches)) await JsonFiles.WriteAsync(patches, new RepairCatalog(1, []), cancellationToken).ConfigureAwait(false);
        var legacy = ManagedPaths.Resolve(Root, "state.json");
        ManagedPaths.CheckLink(legacy);
        await JsonFiles.WriteAsync(ManagedPaths.Resolve(Root, "profiles.json"),
            new ProfilesDocument(state.SchemaVersion, state.Profiles, state.ActiveProfileId), cancellationToken).ConfigureAwait(false);
        await JsonFiles.WriteAsync(ManagedPaths.Resolve(Root, "library.json"),
            new LibraryDocument(state.SchemaVersion, state.Mods, state.UpdateChecks), cancellationToken).ConfigureAwait(false);
        try { File.Delete(legacy); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

    }
    public ValueTask<IAsyncDisposable> AcquireLockAsync(CancellationToken cancellationToken = default) =>
        JsonFiles.LockAsync(ManagedPaths.Resolve(Root, "library.lock"), cancellationToken);
}

internal static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    public static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        ManagedPaths.CheckLink(path);
        await using var file = File.OpenRead(path);
        try { return await JsonSerializer.DeserializeAsync<T>(file, Options, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Empty JSON document."); }
        catch (JsonException ex) { throw new InvalidDataException($"Invalid JSON file: {path}", ex); }
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ManagedPaths.CheckLink(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, value, Options, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static ValueTask<IAsyncDisposable> LockAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ManagedPaths.CheckLink(path);
        // Fail promptly if another process is modifying this library/target.
        return ValueTask.FromResult<IAsyncDisposable>(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }
}

/// <summary>Append-only structured diagnostics. Logging failures don't invalidate completed operations.</summary>
public sealed class JsonEventLog(string directory)
{
    private static readonly SemaphoreSlim gate = new(1, 1);
    public async Task AppendAsync(string operation, string status, string? detail = null, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var path = ManagedPaths.Resolve(Path.GetFullPath(directory), "log.jsonl");
            var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, operation, status, detail });
            await File.AppendAllTextAsync(path, line + "\n", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { gate.Release(); }
    }
}
