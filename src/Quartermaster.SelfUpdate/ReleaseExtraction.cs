using System.IO.Compression;

namespace Quartermaster.SelfUpdate;

internal static class ReleaseExtraction
{
    public static async Task ExtractAsync(string archivePath, string destination, SelfUpdateOptions options, IProgress<SelfUpdateProgress>? progress, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > options.MaxFiles) throw new InvalidDataException("The release contains too many archive entries.");
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Relative, bool Directory)>();
        long length = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            var relative = directory ? name[..^1] : name;
            var parts = relative.Split('/');
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (parts.Any(InvalidPart) || unixType is not (0 or 0x8000 or 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (unixType == 0x4000 && !directory) ||
                parts[0].StartsWith(".quartermaster-update-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release contains an unsafe path or link.");
            if (directory)
            {
                if (entry.Length != 0 || files.Contains(relative)) throw new InvalidDataException("The release has conflicting file and directory entries.");
                directories.Add(relative);
            }
            else
            {
                if (!files.Add(relative) || directories.Contains(relative)) throw new InvalidDataException("The release contains duplicate or conflicting files.");
                if (entry.Length < 0 || entry.Length > options.MaxExtractedBytes - length) throw new InvalidDataException("The expanded release exceeds the size limit.");
                length += entry.Length;
            }
            for (var index = 1; index < parts.Length; index++)
            {
                var parent = string.Join('/', parts.Take(index));
                if (files.Contains(parent)) throw new InvalidDataException("A release file occupies a directory path.");
                directories.Add(parent);
            }
            entries.Add((entry, relative, directory));
        }
        if (!files.Contains(options.ExecutableName) || (options.UsesWindowsUpdater && !files.Contains("winupdater/" + options.UpdaterName)))
            throw new InvalidDataException("The release is missing the application or native updater.");
        // Preserve the expected casing even on case-sensitive installations.
        if (!entries.Any(item => !item.Directory && item.Relative == options.ExecutableName) ||
            (options.UsesWindowsUpdater && !entries.Any(item => !item.Directory && item.Relative == "winupdater/" + options.UpdaterName)))
            throw new InvalidDataException("The release uses incorrect executable filenames.");
        Directory.CreateDirectory(destination);
        long extracted = 0;
        foreach (var (entry, relative, directory) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (directory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var input = entry.Open();
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            var buffer = new byte[65536]; long written = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                written += count;
                if (written > entry.Length) throw new InvalidDataException("An archive entry exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new InvalidDataException("An archive entry is incomplete.");
            extracted += written; progress?.Report(new(SelfUpdatePhase.Extracting, extracted, length, relative));
        }
    }
    private static bool InvalidPart(string part)
    {
        if (string.IsNullOrEmpty(part) || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.Any(character => char.IsControl(character) || ":*?\"<>|".Contains(character))) return true;
        var basename = part.Split('.')[0].ToUpperInvariant();
        return basename is "CON" or "PRN" or "AUX" or "NUL" ||
            basename.Length == 4 && basename[3] is >= '1' and <= '9' && (basename.StartsWith("COM", StringComparison.Ordinal) || basename.StartsWith("LPT", StringComparison.Ordinal));
    }
}
