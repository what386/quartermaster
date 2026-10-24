using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Repatcher.Formats;
using global::System.IO.Compression;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;

namespace Quartermaster.Library.Importing;

public sealed record ImportLimits(int MaxFiles = 10000, long MaxBytes = 8L * 1024 * 1024 * 1024);

public sealed class ModContentStore : IModContentStore
{
    private readonly string root;
    private readonly ImportLimits limits;
    public string ApplicationDirectory { get; }
    public ModContentStore(string applicationDirectory, ImportLimits? limits = null)
    {
        ApplicationDirectory = Path.GetFullPath(applicationDirectory);
        root = Path.Combine(ApplicationDirectory, "library");
        this.limits = limits ?? new();
        if (this.limits.MaxFiles <= 0 || this.limits.MaxBytes <= 0) throw new ArgumentException("Import limits must be positive.");
    }
    public string GetModDirectory(Guid id) => ManagedPaths.Resolve(root, id.ToString("N"));
    public string GetFilePath(Guid id, PatchFile file) => ManagedPaths.Resolve(GetModDirectory(id), file.RelativePath);

    /// <summary>Resolves optional artwork from the stored manifest, including older imports.</summary>
    public string? GetIconPath(Mod mod)
    {
        var source = ReadManifest(mod);
        return source is { } value ? ResolveImage(value.Directory, value.Manifest.IconPath) : null;
    }
    public IReadOnlyList<ModOptionImages> GetOptionImages(Mod mod)
    {
        if (ReadManifest(mod) is not { } source) return [];
        var options = source.Manifest.Options ?? [];
        return mod.Options.Select((option, index) =>
        {
            var metadata = index < options.Count && options[index].Name == option.Name ? options[index] : null;
            var choices = metadata?.SubOptions ?? [];
            return new ModOptionImages(option.Id, ResolveImage(source.Directory, metadata?.Image),
                option.Choices.Select((choice, choiceIndex) =>
                {
                    var item = choiceIndex < choices.Count && choices[choiceIndex].Name == choice.Name ? choices[choiceIndex] : null;
                    return new ModChoiceImage(ResolveImage(source.Directory, item?.Image), item?.Description ?? "");
                }).ToArray());
        }).ToArray();
    }
    private (string Directory, Manifest Manifest)? ReadManifest(Mod mod)
    {
        try
        {
            var manifestPath = ManagedPaths.Enumerate(GetModDirectory(mod.Id)).SingleOrDefault(path =>
                Path.GetFileName(path).Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
            if (manifestPath is null) return null;
            var manifest = global::System.Text.Json.JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonFiles.Options);
            return manifest is null ? null : (Path.GetDirectoryName(manifestPath)!, manifest);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or global::System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        { return null; }
    }
    private static string? ResolveImage(string directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        try
        {
            var path = ManagedPaths.Resolve(directory, relative.Replace('\\', '/'));
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return null; }
    }

    public async Task<byte[]> ReadVerifiedAsync(Guid modId, PatchFile file, CancellationToken ct)
    {
        var path = GetFilePath(modId, file);
        ManagedPaths.CheckLink(path);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.LongLength != file.Size || !Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source file changed while reading.");
        return bytes;
    }

    /// <summary>Exports every repaired patch set and its metadata without changing library files.</summary>
    public async Task<IReadOnlyList<string>> ExportRepatchedAsync(Mod mod, string destination, IPatchRepairer repairer, CancellationToken ct = default)
    {
        destination = Path.GetFullPath(destination);
        var parent = ManagedPaths.CanonicalDirectory(Path.GetDirectoryName(destination)!);
        destination = Path.Combine(parent, Path.GetFileName(destination));
        var relative = Path.GetRelativePath(Path.GetDirectoryName(root)!, destination);
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("Export outside the library storage directory.");
        ManagedPaths.CheckLink(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var patchFiles = mod.PatchSets.Where(set => set.ContentKind == ModContentKind.ArchivePatch).SelectMany(p => p.Files).ToDictionary(f => f.RelativePath, StringComparer.Ordinal);
        var fileModFiles = mod.PatchSets.Where(set => set.ContentKind != ModContentKind.ArchivePatch).SelectMany(set => set.Files).DistinctBy(file => file.RelativePath).ToArray();
        var warnings = new List<string>();
        try
        {
            foreach (var file in fileModFiles)
                await FileIntegrity.VerifyAsync(GetFilePath(mod.Id, file), file.Size, file.Sha256, ct).ConfigureAwait(false);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var patch in mod.PatchSets.Where(set => set.ContentKind == ModContentKind.ArchivePatch))
                    {
                        ct.ThrowIfCancellationRequested();
                        var payloads = new Dictionary<PatchFileKind, ReadOnlyMemory<byte>>();
                        foreach (var file in patch.Files)
                            payloads[file.Kind] = await ReadVerifiedAsync(mod.Id, file, ct).ConfigureAwait(false);
                        var input = new PatchSetData(payloads[PatchFileKind.Main],
                            payloads.TryGetValue(PatchFileKind.GpuResources, out var gpu) ? gpu : (ReadOnlyMemory<byte>?)null,
                            payloads.TryGetValue(PatchFileKind.Stream, out var sourceStream) ? sourceStream : (ReadOnlyMemory<byte>?)null);
                        var result = repairer.RepairSet(input, ct);
                        if (result.RemovedUnits > 0) throw new InvalidDataException("Repatching would remove missing units. Use an updated mod.");
                        warnings.AddRange(result.Warnings);
                        payloads[PatchFileKind.Main] = result.Output.Main;
                        if (result.Output.Gpu is { } repairedGpu) payloads[PatchFileKind.GpuResources] = repairedGpu;
                        else if (payloads.ContainsKey(PatchFileKind.GpuResources)) throw new InvalidDataException("Repair unexpectedly removed a companion.");
                        if (result.Output.Stream is { } repairedStream) payloads[PatchFileKind.Stream] = repairedStream;
                        else if (payloads.ContainsKey(PatchFileKind.Stream)) throw new InvalidDataException("Repair unexpectedly removed a companion.");
                        var mainPath = patch.Files.Single(f => f.Kind == PatchFileKind.Main).RelativePath;
                        foreach (var (kind, bytes) in payloads)
                        {
                            var name = patch.Files.SingleOrDefault(f => f.Kind == kind)?.RelativePath ?? mainPath + PatchFiles.Suffix(kind);
                            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                            await using var stream = entry.Open();
                            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                        }
                    }
                    foreach (var path in ManagedPaths.Enumerate(GetModDirectory(mod.Id)))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetRelativePath(GetModDirectory(mod.Id), path).Replace('\\', '/');
                        if (patchFiles.ContainsKey(name)) continue;
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        await using var stream = entry.Open();
                        await using var input = File.OpenRead(path);
                        await input.CopyToAsync(stream, ct).ConfigureAwait(false);
                    }
                }
                await output.FlushAsync(ct).ConfigureAwait(false); output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            return warnings.Distinct().ToArray();
        }
        catch (InvalidDataException ex)
        { throw new InvalidDataException($"Could not repatch mod '{mod.Name}': {ex.Message}", ex); }
        catch (IOException ex)
        { throw new IOException($"Could not export mod '{mod.Name}': {ex.Message}", ex); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<Mod> ImportAsync(string source, string? name = null, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        ManagedPaths.CheckLink(source);
        var id = Guid.NewGuid();
        ManagedPaths.CheckLink(root); Directory.CreateDirectory(root);
        var temporary = TemporaryStorage.PathFor(ApplicationDirectory, "import-" + id.ToString("N"));
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
                var relativeTemporary = Path.GetRelativePath(source, temporary);
                if (relativeTemporary == "." || (!Path.IsPathRooted(relativeTemporary) && relativeTemporary != ".." && !relativeTemporary.StartsWith(".." + Path.DirectorySeparatorChar)))
                    throw new ArgumentException("Cannot import an ancestor of temporary storage.");
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
            else if (Path.GetFileName(source).StartsWith("generated_", StringComparison.Ordinal) && source.EndsWith(".dl_bin", StringComparison.Ordinal) || source.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))
            {
                await using var input = File.OpenRead(source);
                await Copy(input, Path.GetFileName(source), input.Length).ConfigureAwait(false);
            }
            else throw new ArgumentException("Import source must be a directory, ZIP, generated config or file-mod stream.");
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
            for (var index = 0; index < sets.Count; index++)
                sets[index] = await FileModMetadata.ClassifyAsync(temporary, sets[index], cancellationToken).ConfigureAwait(false);
            sets.AddRange(await FileModMetadata.LooseSetsAsync(temporary, files, sets, cancellationToken).ConfigureAwait(false));
            if (sets.Count == 0) throw new InvalidDataException("Import contains no main patch files or supported file-mod payloads.");
            var (manifest, options) = await ManifestReader.ReadAsync(temporary, files, sets, cancellationToken).ConfigureAwait(false);
            var displayName = name ?? manifest?.Name ?? (Directory.Exists(source) ? new DirectoryInfo(source).Name : Path.GetFileNameWithoutExtension(source));
            var mod = new Mod(id, displayName.Trim(), manifest?.Description ?? "", manifest?.ModVersion,
                Guid.TryParse(manifest?.Guid, out var manifestId) ? manifestId : null, DateTimeOffset.UtcNow, sets.ToArray(), options.Count > 0 ? options : FileModMetadata.InferOptions(sets), [])
            { FileDetectionVersion = FileModMetadata.Version, ImportedFileName = Directory.Exists(source) ? null : Path.GetFileName(source), Tags = ModTags.Normalize(manifest?.Tags ?? []) };
            StateValidation.Validate(LibraryState.Empty with { Mods = [mod] });
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporary, GetModDirectory(id));
            return mod;
        }
        catch (InvalidDataException ex)
        { throw new InvalidDataException($"Could not import mod '{name ?? Path.GetFileName(source)}' ({source}): {ex.Message}", ex); }
        catch (IOException ex)
        { throw new IOException($"Could not import mod '{name ?? Path.GetFileName(source)}' ({source}): {ex.Message}", ex); }
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
