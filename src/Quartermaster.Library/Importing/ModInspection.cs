using Quartermaster.Core.Patching;
using global::System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Storage;
using global::System.Buffers.Binary;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Library.Importing;

internal sealed record Manifest(int Version = 1, string? Guid = null, string? Name = null,
    string? Description = null, string? ModVersion = null, IReadOnlyList<ManifestOption>? Options = null, string? IconPath = null);
internal sealed record ManifestOption(string Name = "", string Description = "", IReadOnlyList<string>? Include = null,
    IReadOnlyList<ManifestChoice>? SubOptions = null, string? Image = null);
internal sealed record ManifestChoice(string Name = "", IReadOnlyList<string>? Include = null, string? Image = null, string Description = "");

internal static class ManifestReader
{
    public static async Task<(Manifest? Manifest, IReadOnlyList<ModOption> Options)> ReadAsync(
        string root, IReadOnlyList<string> files, IReadOnlyList<PatchSet> sets, CancellationToken ct)
    {
        var manifests = files.Where(f => Path.GetFileName(f).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length == 0) return (null, []);
        if (manifests.Length > 1) throw new InvalidDataException("Import contains multiple mod manifests.");
        var manifest = await JsonFiles.ReadAsync<Manifest>(ManagedPaths.Resolve(root, manifests[0]), ct).ConfigureAwait(false);
        if (manifest.Version != 1) throw new InvalidDataException("Unsupported manifest version.");
        var baseFolder = Path.GetDirectoryName(manifests[0])?.Replace('\\', '/') ?? "";
        IReadOnlyList<Guid> Resolve(IReadOnlyList<string>? folders)
        {
            var ids = new HashSet<Guid>();
            var ordered = new List<Guid>();
            foreach (var raw in folders ?? [])
            {
                var folder = raw.Replace('\\', '/').TrimEnd('/');
                if (folder != "" && !PatchValidation.IsRelativePath(folder)) throw new InvalidDataException("Unsafe manifest Include path.");
                var prefix = baseFolder == "" ? folder : folder == "" ? baseFolder : baseFolder + "/" + folder;
                var matches = sets.Where(s => prefix == "" || s.Folder == prefix || s.Folder.StartsWith(prefix + "/", StringComparison.Ordinal)).ToArray();
                if (matches.Length == 0) throw new InvalidDataException($"Manifest Include matches no patch sets: {raw}");
                foreach (var set in matches) if (ids.Add(set.Id)) ordered.Add(set.Id);
            }
            return ordered.ToArray();
        }
        var options = (manifest.Options ?? []).Select(o => new ModOption(Guid.NewGuid(), o.Name, o.Description,
            Resolve(o.Include), (o.SubOptions ?? []).Select(c => new OptionChoice(c.Name, Resolve(c.Include))).ToArray())).ToArray();
        return (manifest, options);
    }
}

public static class PatchInspector
{
    public static async Task<IReadOnlyList<ResourceKey>> InspectAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        var header = new byte[72];
        await file.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var types = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var records = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        var size = 72L + types * 32L + records * 80L;
        if (size > file.Length || size > int.MaxValue) throw new InvalidDataException("Invalid patch table length.");
        var data = new byte[(int)size];
        header.CopyTo(data, 0);
        await file.ReadExactlyAsync(data.AsMemory(72), ct).ConfigureAwait(false);
        var table = PatchTable.Read(data);
        ulong end = (ulong)table.DataStart;
        foreach (var record in table.Resources.Where(r => r.DataSize > 0).OrderBy(r => r.DataOffset))
        {
            if (record.DataOffset < end || record.DataOffset > (ulong)file.Length || record.DataSize > (ulong)file.Length - record.DataOffset)
                throw new InvalidDataException("Invalid or overlapping patch resource range.");
            end = record.DataOffset + record.DataSize;
        }
        foreach (var record in table.Resources)
        {
            ValidateCompanion(path + ".stream", record.StreamOffset, record.StreamSize);
            ValidateCompanion(path + ".gpu_resources", record.GpuOffset, record.GpuSize);
        }
        return table.Resources.Select(r => new ResourceKey(r.FileId, r.TypeId)).Distinct().ToArray();
    }
    private static void ValidateCompanion(string path, ulong offset, uint size)
    {
        if (size == 0) return;
        if (!File.Exists(path)) throw new InvalidDataException("Patch references a missing companion file.");
        var length = (ulong)new FileInfo(path).Length;
        if (offset > length || size > length - offset) throw new InvalidDataException("Patch resource extends beyond its companion file.");
    }
}
