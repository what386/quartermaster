using global::System.IO.Compression;
using global::System.Text.Json;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library.Profiles;

/// <summary>Portable profiles include original mod files, metadata and every option variant.</summary>
public sealed class ProfileArchives(ILibraryStore store, ModContentStore contents, ImportLimits? limits = null)
{
    private sealed record Document(int Version, Profile Profile, IReadOnlyList<Mod> Mods);
    private readonly ImportLimits limits = limits ?? new();

    public static bool IsProfileArchive(string source)
    {
        if (Directory.Exists(source) || !Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        ManagedPaths.CheckLink(source);
        using var zip = ZipFile.OpenRead(source);
        return zip.GetEntry("profile.json") is not null;
    }

    public async Task ExportAsync(Guid profileId, string destination, CancellationToken ct = default)
    {
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        var profile = state.Profiles.SingleOrDefault(item => item.Id == profileId) ?? throw new KeyNotFoundException("Profile does not exist.");
        var mods = profile.Entries.Select(entry => state.Mods.Single(mod => mod.Id == entry.ModId)).ToArray();
        var document = new Document(1, profile, mods);
        Validate(document);
        destination = Path.GetFullPath(destination);
        destination = Path.Combine(ManagedPaths.CanonicalDirectory(Path.GetDirectoryName(destination)!), Path.GetFileName(destination));
        var applicationDirectory = contents.ApplicationDirectory;
        var relative = Path.GetRelativePath(applicationDirectory, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("Export outside the library storage directory.");
        ManagedPaths.CheckLink(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using (var metadata = zip.CreateEntry("profile.json").Open())
                        await JsonSerializer.SerializeAsync(metadata, document, JsonFiles.Options, ct).ConfigureAwait(false);
                    foreach (var mod in mods)
                    {
                        var directory = contents.GetModDirectory(mod.Id);
                        var patches = mod.PatchSets.SelectMany(set => set.Files).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var path in ManagedPaths.Enumerate(directory))
                        {
                            ct.ThrowIfCancellationRequested();
                            var name = Path.GetRelativePath(directory, path).Replace('\\', '/');
                            if (patches.TryGetValue(name, out var file))
                            {
                                await FileIntegrity.VerifyAsync(path, file.Size, file.Sha256, ct).ConfigureAwait(false);
                                seen.Add(name);
                            }
                            await using var input = File.OpenRead(path);
                            await using var entry = zip.CreateEntry($"mods/{mod.Id:N}/{name}", CompressionLevel.Optimal).Open();
                            await input.CopyToAsync(entry, ct).ConfigureAwait(false);
                        }
                        if (seen.Count != patches.Count) throw new IOException("Mod files are missing from library storage.");
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<Profile> ImportAsync(string source, CancellationToken ct = default)
    {
        if (limits.MaxFiles <= 0 || limits.MaxBytes <= 0) throw new ArgumentException("Import limits must be positive.");
        source = Path.GetFullPath(source); ManagedPaths.CheckLink(source);
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        var temporary = TemporaryStorage.PathFor(contents.ApplicationDirectory, "profile-" + Guid.NewGuid().ToString("N"));
        var created = new List<Guid>();
        Directory.CreateDirectory(temporary);
        try
        {
            Document document;
            using (var zip = ZipFile.OpenRead(source))
            {
                var metadata = zip.Entries.SingleOrDefault(entry => entry.FullName == "profile.json")
                    ?? throw new InvalidDataException("Archive does not contain a Quartermaster profile.");
                if (metadata.Length > Math.Min(limits.MaxBytes, 16 * 1024 * 1024)) throw new InvalidDataException("Profile metadata exceeds import limits.");
                await using (var input = metadata.Open())
                    document = await JsonSerializer.DeserializeAsync<Document>(input, JsonFiles.Options, ct).ConfigureAwait(false)
                        ?? throw new InvalidDataException("Profile metadata is empty.");
                Validate(document);
                var prefixes = document.Mods.Select(mod => $"mods/{mod.Id:N}/").ToArray();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long bytes = 0;
                foreach (var entry in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = entry.FullName;
                    if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || !names.Add(name) || names.Count > limits.MaxFiles)
                        throw new InvalidDataException("Archive contains links, duplicate files or too many files.");
                    var path = ManagedPaths.Resolve(temporary, name.TrimEnd('/'));
                    if (name.EndsWith('/')) continue;
                    if (name != "profile.json" && !prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
                        throw new InvalidDataException("Archive contains files outside its profile mods.");
                    if (entry.Length < 0 || entry.Length > limits.MaxBytes - bytes) throw new InvalidDataException("Archive exceeds import limits.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var input = entry.Open();
                    await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920]; long written = 0; int count;
                    while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        written += count;
                        if (written > entry.Length || written > limits.MaxBytes - bytes) throw new InvalidDataException("Archive file exceeds its declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    }
                    if (written != entry.Length) throw new InvalidDataException("Archive file is truncated.");
                    bytes += written;
                }
            }
            var mods = state.Mods.ToList();
            var mapping = new Dictionary<Guid, Mod>();
            foreach (var original in document.Mods)
            {
                var imported = await contents.ImportAsync(Path.Combine(temporary, "mods", original.Id.ToString("N")), original.Name, ct).ConfigureAwait(false);
                created.Add(imported.Id);
                var identity = ModIdentity.GetKey(imported);
                if (identity != ModIdentity.GetKey(original)) throw new InvalidDataException($"Mod content does not match profile metadata: {original.Name}");
                var existing = mods.FirstOrDefault(mod => ModIdentity.GetKey(mod) == identity);
                if (existing is not null)
                {
                    await contents.DeleteAsync(imported.Id, CancellationToken.None).ConfigureAwait(false);
                    created.Remove(imported.Id); mapping.Add(original.Id, existing);
                }
                else
                {
                    imported = imported with { Description = original.Description, Version = original.Version, Sources = original.Sources };
                    mods.Add(imported); mapping.Add(original.Id, imported);
                }
            }
            var groups = document.Profile.Groups.ToDictionary(group => group.Id, _ => Guid.NewGuid());
            var importedProfile = document.Profile with
            {
                Id = Guid.NewGuid(),
                Groups = document.Profile.Groups.Select(group => group with { Id = groups[group.Id] }).ToArray(),
                Entries = document.Profile.Entries.Select(entry =>
                {
                    var original = document.Mods.Single(mod => mod.Id == entry.ModId);
                    var mod = mapping[entry.ModId];
                    var options = original.Options.Select((option, index) => (option.Id, NewId: mod.Options[index].Id)).ToDictionary(option => option.Id, option => option.NewId);
                    return entry with
                    {
                        ModId = mod.Id,
                        GroupId = entry.GroupId is { } groupId ? groups[groupId] : null,
                        Options = entry.Options.Select(option => option with { OptionId = options[option.OptionId] }).ToArray()
                    };
                }).ToArray()
            };
            await store.SaveAsync(state with { Mods = mods.ToArray(), Profiles = [.. state.Profiles, importedProfile] }, ct).ConfigureAwait(false);
            created.Clear();
            return importedProfile;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { throw new InvalidDataException("Invalid profile archive.", ex); }
        finally
        {
            foreach (var id in created) await contents.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void Validate(Document document)
    {
        if (document.Version != 1) throw new InvalidDataException("Unsupported profile archive version.");
        StateValidation.Validate(LibraryState.Empty with { Mods = document.Mods, Profiles = [document.Profile] });
        if (document.Mods.Count != document.Profile.Entries.Count) throw new InvalidDataException("Archive includes mods outside the profile.");
        var identities = document.Mods.Select(ModIdentity.GetKey).ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new InvalidDataException("Profile archive contains duplicate mods.");
    }
}
