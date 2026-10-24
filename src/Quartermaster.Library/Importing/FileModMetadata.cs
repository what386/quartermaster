using Quartermaster.Core.Patching;
using System.Buffers.Binary;
using Quartermaster.Repatcher.Formats;
using Quartermaster.FileMerger;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Storage;
using System.Security.Cryptography;
using System.Text;

namespace Quartermaster.Library.Importing;

internal static class FileModMetadata
{
    internal const int Version = 1;
    internal static async Task<PatchSet> ClassifyAsync(string root, PatchSet set, CancellationToken ct)
    {
        if (set.ContentKind != ModContentKind.ArchivePatch || !set.Resources.Any(resource => resource.Type == 0xa14e8dfa2cd117e2)) return set;
        var main = set.Files.Single(file => file.Kind == PatchFileKind.Main);
        var path = ManagedPaths.Resolve(root, main.RelativePath);
        await FileIntegrity.VerifyAsync(path, main.Size, main.Sha256, ct).ConfigureAwait(false);
        // Inspect resource prefixes first: ordinary Lua can share very large asset patches.
        // Such patches must not inherit the config detector's 256 MiB payload limit.
        await using (var input = File.OpenRead(path))
        {
            var header = new byte[72]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            var length = 72L + 32L * BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) + 80L * BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (length > main.Size || length > int.MaxValue) throw new InvalidDataException("Invalid patch resource table.");
            var tableBytes = new byte[(int)length]; header.CopyTo(tableBytes, 0);
            await input.ReadExactlyAsync(tableBytes.AsMemory(72), ct).ConfigureAwait(false);
            var recognized = false;
            foreach (var resource in PatchTable.Read(tableBytes).Resources.Where(resource => resource.TypeId == 0xa14e8dfa2cd117e2))
            {
                if (resource.DataOffset > (ulong)main.Size || resource.DataSize > (ulong)main.Size - resource.DataOffset)
                    throw new InvalidDataException("Patch resource is outside its source file.");
                var prefix = new byte[(int)Math.Min(resource.DataSize, 48)];
                input.Position = (long)resource.DataOffset; await input.ReadExactlyAsync(prefix, ct).ConfigureAwait(false);
                if (prefix.Length >= 8 && (prefix.AsSpan(8).StartsWith("HD2FM1"u8) || prefix.AsSpan(8).StartsWith("-- HD2 file-mod installer 1 "u8)))
                { recognized = true; break; }
            }
            if (!recognized) return set;
        }
        if (main.Size > new DetectionLimits().MaxFileBytes) throw new InvalidDataException($"File-mod payload exceeds detection size limits: {main.RelativePath}");
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length != main.Size || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), main.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"File-mod detection source changed: {main.RelativePath}");
        var detection = new FileModDetector().DetectFile(main.RelativePath, bytes, ct);
        if (detection.Mods.Count == 0 && detection.InstallerFiles.Count == 0) return set;
        if (detection.Mods.Count + detection.InstallerFiles.Count != set.Resources.Count)
            throw new InvalidDataException($"File-mod patch mixes installer payloads with unsupported resources: {main.RelativePath}");
        return set with
        {
            ContentKind = detection.Mods.Count > 0 ? ModContentKind.FileMod : ModContentKind.FileModInstaller,
            FileModTarget = detection.Mods.Count == 1 ? detection.Mods[0].Target : null,
            FileModVariantName = detection.Mods.Count == 1 ? detection.Mods[0].Name : null
        };
    }

    internal static async Task<IReadOnlyList<PatchSet>> LooseSetsAsync(string root, IReadOnlyList<string> files, IReadOnlyList<PatchSet> existing, CancellationToken ct)
    {
        var owned = existing.SelectMany(set => set.Files).Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
        var sets = new List<PatchSet>();
        foreach (var file in files.Where(file => !owned.Contains(file) &&
            (Path.GetFileName(file).StartsWith("generated_", StringComparison.Ordinal) && file.EndsWith(".dl_bin", StringComparison.Ordinal) || file.EndsWith(".stream", StringComparison.OrdinalIgnoreCase))))
        {
            var path = ManagedPaths.Resolve(root, file);
            var detected = await new FileModDetector().DetectAsync(path, ct).ConfigureAwait(false);
            if (detected.Mods.Count == 0) continue;
            var stored = new PatchFile(file, PatchFileKind.Main, new FileInfo(path).Length, await FileIntegrity.HashAsync(path, ct).ConfigureAwait(false));
            var archive = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(file)))[..16];
            for (var index = 0; index < detected.Mods.Count; index++)
                sets.Add(new(Guid.NewGuid(), archive, 0, Path.GetDirectoryName(file)?.Replace('\\', '/') ?? "", [stored], [])
                { ContentKind = ModContentKind.FileMod, FileModPayloadIndex = index,
                    FileModTarget = detected.Mods[index].Target, FileModVariantName = detected.Mods[index].Name });
        }
        return sets;
    }

    internal static IReadOnlyList<ModOption> InferOptions(IReadOnlyList<PatchSet> sets)
    {
        var fileSets = sets.Where(set => set.ContentKind == ModContentKind.FileMod).ToArray();
        var options = new List<ModOption>();
        var assigned = new HashSet<Guid>();
        // Several frames for one target in one stream are alternatives, not independent files.
        foreach (var group in fileSets.GroupBy(set => (File: set.Files.Single(file => file.Kind == PatchFileKind.Main).RelativePath, set.FileModTarget))
            .Where(group => group.Key.FileModTarget is not null && group.Count() > 1))
        {
            var choices = group.Select((set, index) =>
            {
                assigned.Add(set.Id);
                return new OptionChoice($"{set.FileModVariantName} ({index + 1})", [set.Id]);
            }).ToArray();
            options.Add(new(Guid.NewGuid(), Path.GetFileName(group.Key.File), "Choose a file-mod variant.", [], choices));
        }
        var remaining = fileSets.Where(set => !assigned.Contains(set.Id) && set.Folder.Length > 0).GroupBy(set => set.Folder).ToList();
        while (remaining.Count > 0)
        {
            var cluster = new List<IGrouping<string, PatchSet>> { remaining[0] }; remaining.RemoveAt(0);
            var targets = cluster[0].Select(set => set.FileModTarget ?? set.Archive).ToHashSet(StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                for (var index = remaining.Count - 1; index >= 0; index--)
                {
                    if (!remaining[index].Any(set => targets.Contains(set.FileModTarget ?? set.Archive))) continue;
                    var group = remaining[index]; remaining.RemoveAt(index); cluster.Add(group);
                    targets.UnionWith(group.Select(set => set.FileModTarget ?? set.Archive)); changed = true;
                }
            } while (changed);
            // Folders editing different files are independent features and remain selected together.
            if (cluster.Count > 1)
                options.Add(new(Guid.NewGuid(), "Variant", "Choose a file-mod variant.", [], cluster.OrderBy(group => group.Key, StringComparer.Ordinal)
                    .Select(group => new OptionChoice(group.Key, group.Select(set => set.Id).ToArray())).ToArray()));
        }
        return options;
    }
}
