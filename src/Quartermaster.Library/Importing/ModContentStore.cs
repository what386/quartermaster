using Quartermaster.Core.Patching;
using global::System.IO.Compression;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library.Importing;

public sealed record ImportLimits(int MaxFiles = 10000, long MaxBytes = 8L * 1024 * 1024 * 1024);

public sealed class ModContentStore : IModContentStore
{
    private readonly string root;
    private readonly ImportLimits limits;
    public ModContentStore(string applicationDirectory, ImportLimits? limits = null)
    {
        root = Path.Combine(Path.GetFullPath(applicationDirectory), "library");
        this.limits = limits ?? new();
        if (this.limits.MaxFiles <= 0 || this.limits.MaxBytes <= 0) throw new ArgumentException("Import limits must be positive.");
    }
    public string GetModDirectory(Guid id) => ManagedPaths.Resolve(root, id.ToString("N"));
    public string GetFilePath(Guid id, PatchFile file) => ManagedPaths.Resolve(GetModDirectory(id), file.RelativePath);

    public async Task<Mod> ImportAsync(string source, string? name = null, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        ManagedPaths.CheckLink(source);
        var id = Guid.NewGuid();
        var temporary = ManagedPaths.Resolve(root, ".import-" + id.ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long bytes = 0;
            async Task Copy(Stream input, string relative, long length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!names.Add(relative) || names.Count > limits.MaxFiles || length < 0 || length > limits.MaxBytes - bytes)
                    throw new InvalidDataException("Duplicate file or import limit exceeded.");
                var destination = ManagedPaths.Resolve(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += count;
                    if (written > length || written > limits.MaxBytes - bytes) throw new InvalidDataException("Import stream exceeds declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (written != length) throw new InvalidDataException("Import stream is truncated.");
                bytes += written;
            }
            if (Directory.Exists(source))
            {
                var relativeRoot = Path.GetRelativePath(source, root);
                if (relativeRoot == "." || (!Path.IsPathRooted(relativeRoot) && relativeRoot != ".." && !relativeRoot.StartsWith(".." + Path.DirectorySeparatorChar)))
                    throw new ArgumentException("Cannot import an ancestor of library storage.");
                foreach (var file in ManagedPaths.Enumerate(source))
                {
                    await using var input = File.OpenRead(file);
                    await Copy(input, Path.GetRelativePath(source, file).Replace('\\', '/'), input.Length).ConfigureAwait(false);
                }
            }
            else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(source);
                foreach (var entry in zip.Entries)
                {
                    if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("ZIP contains a symbolic link.");
                    var relative = entry.FullName.Replace('\\', '/');
                    if (relative.EndsWith('/')) { ManagedPaths.Resolve(temporary, relative.TrimEnd('/')); continue; }
                    await using var input = entry.Open();
                    await Copy(input, relative, entry.Length).ConfigureAwait(false);
                }
            }
            else throw new ArgumentException("Import source must be a directory or ZIP file.");
            var files = names.Order(StringComparer.Ordinal).ToArray();
            var groups = new Dictionary<(string Folder, string Archive, int Slot), List<(string Path, PatchFileKind Kind)>>();
            foreach (var file in files)
            {
                if (!PatchNames.TryParse(Path.GetFileName(file), out var archive, out var slot, out var kind)) continue;
                var folder = Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "";
                var key = (folder, archive, slot);
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                group.Add((file, kind));
            }
            var sets = new List<PatchSet>();
            foreach (var (key, group) in groups.OrderBy(g => g.Key.Folder, StringComparer.Ordinal).ThenBy(g => g.Key.Archive).ThenBy(g => g.Key.Slot))
            {
                if (group.All(f => f.Kind != PatchFileKind.Main)) continue;
                if (group.Select(f => f.Kind).Distinct().Count() != group.Count) throw new InvalidDataException("Duplicate patch-set file kind.");
                var stored = new List<PatchFile>();
                foreach (var file in group.OrderBy(f => f.Kind))
                {
                    var path = ManagedPaths.Resolve(temporary, file.Path);
                    stored.Add(new(file.Path, file.Kind, new FileInfo(path).Length, await FileIntegrity.HashAsync(path, cancellationToken).ConfigureAwait(false)));
                }
                var main = group.Single(f => f.Kind == PatchFileKind.Main);
                sets.Add(new(Guid.NewGuid(), key.Archive, key.Slot, key.Folder, stored.ToArray(),
                    await PatchInspector.InspectAsync(ManagedPaths.Resolve(temporary, main.Path), cancellationToken).ConfigureAwait(false)));
            }
            if (sets.Count == 0) throw new InvalidDataException("Import contains no main patch files.");
            var (manifest, options) = await ManifestReader.ReadAsync(temporary, files, sets, cancellationToken).ConfigureAwait(false);
            var displayName = name ?? manifest?.Name ?? (Directory.Exists(source) ? new DirectoryInfo(source).Name : Path.GetFileNameWithoutExtension(source));
            var mod = new Mod(id, displayName.Trim(), manifest?.Description ?? "", manifest?.ModVersion,
                Guid.TryParse(manifest?.Guid, out var manifestId) ? manifestId : null, DateTimeOffset.UtcNow, sets.ToArray(), options, []);
            StateValidation.Validate(LibraryState.Empty with { Mods = [mod] });
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, GetModDirectory(id));
            return mod;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }
    public Task DeleteAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetModDirectory(modId);
        if (Directory.Exists(path)) { _ = ManagedPaths.Enumerate(path).ToArray(); Directory.Delete(path, recursive: true); }
        return Task.CompletedTask;
    }
}
