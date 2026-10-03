using System.Text.RegularExpressions;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;
using Quartermaster.Repatcher.Repair;

namespace Quartermaster.Repatcher;

public enum RepairStatus { Updated, NoUnits, Corrupted, Failed }
public sealed record PatchRepair(RepairStatus Status, byte[]? Data, int RepairedUnits = 0,
    int RemovedUnits = 0, string? Error = null);
public sealed record FileRepair(string SourcePath, string DestinationPath, RepairStatus Status,
    int RepairedUnits = 0, int RemovedUnits = 0, string? Error = null);
public sealed record BatchRepair(string Directory, IReadOnlyList<FileRepair> Files)
{
    public int PatchesFound => Files.Count;
    public bool HasErrors => Files.Any(f => f.Status is RepairStatus.Corrupted or RepairStatus.Failed);
}

/// <summary>Native hd2-repatcher unit repair. All file operations write to separate staging destinations.</summary>
public sealed partial class Repatcher(IUnitResourceSource resources)
{
    private readonly IUnitResourceSource resources = resources ?? throw new ArgumentNullException(nameof(resources));
    private sealed record Edit(int Offset, int Length, byte[] Replacement);

    /// <summary>Repairs an in-memory patch without changing its source buffer.</summary>
    public PatchRepair RepairPatch(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = patch.Span;
            var table = PatchTable.Read(data);
            var ranges = table.Resources.Where(r => r.DataSize > 0).OrderBy(r => r.DataOffset).ToArray();
            ulong previousEnd = (ulong)table.DataStart;
            foreach (var record in ranges)
            {
                if (record.DataOffset < previousEnd) throw new InvalidDataException("Overlapping resource data or resource inside TOC.");
                BinaryData.Slice(data, BinaryData.Length(record.DataOffset), record.DataSize);
                previousEnd = checked(record.DataOffset + record.DataSize);
            }
            if (!table.Resources.Any(r => r.TypeId == PatchTable.UnitTypeId))
                return new(RepairStatus.NoUnits, data.ToArray());

            var edits = new List<Edit>();
            var retained = new List<ResourceRecord>();
            var repaired = 0;
            var removed = 0;
            for (var i = 0; i < table.Resources.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var record = table.Resources[i];
                if (record.TypeId != PatchTable.UnitTypeId) { retained.Add(record); continue; }
                if (!resources.ContainsUnit(record.FileId))
                {
                    edits.Add(new(table.ItemTableOffset + i * 80, 80, []));
                    removed++;
                    continue;
                }
                var original = resources.ReadUnit(record.FileId, cancellationToken);
                var replacement = UnitRepair.Repair(BinaryData.Slice(data, BinaryData.Length(record.DataOffset), record.DataSize), original);
                edits.Add(new(BinaryData.Length(record.DataOffset), BinaryData.Length(record.DataSize), replacement));
                retained.Add(record with { DataSize = (uint)replacement.Length });
                repaired++;
            }
            edits.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            using var output = new MemoryStream();
            var cursor = 0;
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (edit.Offset < cursor) throw new InvalidDataException("Overlapping repair edits.");
                output.Write(data.Slice(cursor, edit.Offset - cursor));
                output.Write(edit.Replacement);
                cursor = checked(edit.Offset + edit.Length);
            }
            output.Write(data[cursor..]);
            var result = output.ToArray();
            BinaryData.Put32(result, 8, (uint)retained.Count);
            for (var i = 0; i < table.Types.Count; i++)
                if (table.Types[i].TypeId == PatchTable.UnitTypeId)
                    BinaryData.Put64(result, 72 + i * 32 + 16, table.Types[i].Count - (ulong)removed);
            for (var i = 0; i < retained.Count; i++)
            {
                var record = retained[i];
                long delta = 0;
                foreach (var edit in edits)
                    if ((ulong)edit.Offset < record.DataOffset) delta += edit.Replacement.Length - (long)edit.Length;
                var updatedOffset = record.DataOffset == 0 ? 0 : checked((ulong)((long)record.DataOffset + delta));
                (record with { DataOffset = updatedOffset }).Write(result.AsSpan(table.ItemTableOffset + i * 80, 80));
            }
            // Re-read the rebuilt table before any output can be published.
            PatchTable.Read(result);
            return new(RepairStatus.Updated, result, repaired, removed);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        {
            return new(RepairStatus.Corrupted, null, Error: ex.Message);
        }
    }

    /// <summary>Writes the repaired main patch and byte-identical companions into a new staging location.
    /// Destinations must not exist. The main patch is published after its companions.</summary>
    public async Task<FileRepair> RepairFileAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destinationPath = Path.GetFullPath(destinationPath);
        if (PathEquals(sourcePath, destinationPath)) throw new ArgumentException("Repair destination must differ from the source.");
        var pairs = new List<(string Source, string Destination)> { (sourcePath, destinationPath) };
        foreach (var suffix in new[] { ".stream", ".gpu_resources" })
        {
            if (File.Exists(destinationPath + suffix))
                throw new IOException($"Staging destination already exists: {destinationPath + suffix}");
            if (File.Exists(sourcePath + suffix)) pairs.Add((sourcePath + suffix, destinationPath + suffix));
        }
        foreach (var pair in pairs)
            if (File.Exists(pair.Destination)) throw new IOException($"Staging destination already exists: {pair.Destination}");
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var repair = await Task.Run(() => RepairPatch(bytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (repair.Status == RepairStatus.Corrupted)
            return new(sourcePath, destinationPath, repair.Status, Error: repair.Error);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporary = new List<(string Temp, string Final)>();
        var published = new List<string>();
        try
        {
            foreach (var pair in pairs.AsEnumerable().Reverse())
            {
                var temp = pair.Destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                temporary.Add((temp, pair.Destination));
                await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                if (pair.Source == sourcePath) await output.WriteAsync(repair.Data!, cancellationToken).ConfigureAwait(false);
                else
                {
                    await using var input = File.OpenRead(pair.Source);
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var pair in temporary)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pair.Temp, pair.Final, overwrite: false);
                published.Add(pair.Final);
            }
            return new(sourcePath, destinationPath, repair.Status, repair.RepairedUnits, repair.RemovedUnits);
        }
        catch
        {
            foreach (var file in published) File.Delete(file);
            throw;
        }
        finally
        {
            foreach (var pair in temporary) if (File.Exists(pair.Temp)) File.Delete(pair.Temp);
        }
    }

    public static IReadOnlyList<string> FindPatchFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => PatchName().IsMatch(Path.GetFileName(p)))
            .Order(StringComparer.Ordinal).ToArray();

    /// <summary>Processes patches sequentially in deterministic order, preserving relative folder paths.</summary>
    public async Task<BatchRepair> RepairFolderAsync(string sourceDirectory, string stagingDirectory,
        IProgress<FileRepair>? progress = null, CancellationToken cancellationToken = default)
    {
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        stagingDirectory = Path.GetFullPath(stagingDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var relative = Path.GetRelativePath(sourceDirectory, stagingDirectory);
        if (PathEquals(sourceDirectory, stagingDirectory) || (!Path.IsPathRooted(relative) &&
            relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, comparison)))
            throw new ArgumentException("Staging must be outside the source directory.");
        var results = new List<FileRepair>();
        foreach (var file in FindPatchFiles(sourceDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.Combine(stagingDirectory, Path.GetRelativePath(sourceDirectory, file));
            FileRepair result;
            try { result = await RepairFileAsync(file, destination, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { result = new(file, destination, RepairStatus.Failed, Error: ex.Message); }
            results.Add(result);
            progress?.Report(result);
        }
        return new(sourceDirectory, results.AsReadOnly());
    }

    private static bool PathEquals(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a),
        Path.TrimEndingDirectorySeparator(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    [GeneratedRegex(@"^[0-9a-fA-F]{16}\.patch(?:_[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchName();
}
