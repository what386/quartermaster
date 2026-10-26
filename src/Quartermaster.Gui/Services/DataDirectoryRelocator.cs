using System.Security.Cryptography;

namespace Quartermaster.Gui.Services;

public static class DataDirectoryRelocator
{
    private const string MoveSourceMarker = "data-directory-move-source.txt";

    public static string Move(string source, string destination, string pointerFile)
    {
        source = NormalizePath(source);
        destination = NormalizePath(destination);
        pointerFile = NormalizePath(pointerFile);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The current Quartermaster data folder is unavailable.");
        if (string.Equals(source, destination, PathComparison)) throw new InvalidOperationException("Choose a different folder.");
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new InvalidOperationException("The new folder cannot be inside or contain the current data folder.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty folder for the Quartermaster data.");
        var destinationExisted = Directory.Exists(destination);
        Directory.CreateDirectory(destination);
        try
        {
            CopyDirectory(source, destination, pointerFile);
            File.WriteAllText(Path.Combine(destination, MoveSourceMarker), source);
            Directory.CreateDirectory(Path.GetDirectoryName(pointerFile)!);
            var temporary = pointerFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file)) { writer.Write(destination); writer.Flush(); file.Flush(true); }
            File.Move(temporary, pointerFile, overwrite: true);
            return destination;
        }
        catch
        {
            try
            {
                if (Directory.Exists(destination) && !destinationExisted) Directory.Delete(destination, recursive: true);
                else if (Directory.Exists(destination))
                    foreach (var entry in Directory.EnumerateFileSystemEntries(destination))
                        if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true); else File.Delete(entry);
            }
            catch { }
            throw;
        }
    }

    /// <summary>Removes the previous copy only after the new library has opened successfully.</summary>
    public static bool FinalizePreviousMove(string currentDirectory, string pointerFile)
    {
        currentDirectory = NormalizePath(currentDirectory);
        var marker = Path.Combine(currentDirectory, MoveSourceMarker);
        if (!File.Exists(marker)) return true;
        var source = NormalizePath(File.ReadAllText(marker).Trim());
        if (string.Equals(source, currentDirectory, PathComparison) || IsWithin(source, currentDirectory) || IsWithin(currentDirectory, source) ||
            !Directory.Exists(source) || !(File.Exists(Path.Combine(source, "library.json")) || File.Exists(Path.Combine(source, "state.json")))) return false;
        if (!HasNoLinks(source)) return false;
        var lockPath = Path.Combine(source, "app.lock");
        FileStream? previousLock = null;
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (previousLock is null && DateTime.UtcNow < timeout)
        {
            try { previousLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { return false; }
        }
        if (previousLock is null) return false;
        previousLock.Dispose();
        var preservePointer = string.Equals(source, NormalizePath(Path.GetDirectoryName(pointerFile)!), PathComparison);
        DeleteContents(source, preservePointer ? Path.GetFileName(pointerFile) : null);
        if (!preservePointer) Directory.Delete(source);
        File.Delete(marker);
        return true;
    }

    private static bool HasNoLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) return false;
            if (Directory.Exists(entry) && !HasNoLinks(entry)) return false;
        }
        return true;
    }

    private static void DeleteContents(string path, string? preserveFile)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (preserveFile is not null && Path.GetFileName(entry).Equals(preserveFile, PathComparison)) continue;
            if (Directory.Exists(entry)) { DeleteContents(entry, null); Directory.Delete(entry); }
            else File.Delete(entry);
        }
    }

    private static void CopyDirectory(string source, string destination, string pointerFile)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Cannot move linked data: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target, pointerFile);
            }
            else if (string.Equals(Path.GetFullPath(entry), pointerFile, PathComparison))
            {
                // The active-location pointer belongs to this machine and is updated after the copy.
            }
            else if (Path.GetFileName(entry).Equals("app.lock", StringComparison.OrdinalIgnoreCase))
            {
                // The new process must acquire a fresh instance lock.
            }
            else
            {
                File.Copy(entry, target, overwrite: false);
                using var original = File.OpenRead(entry);
                using var copied = File.OpenRead(target);
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(original), SHA256.HashData(copied)))
                    throw new IOException($"A copied data file failed verification: {Path.GetFileName(entry)}");
            }
        }
    }

    private static bool IsWithin(string parent, string child)
    {
        parent = NormalizePath(parent); child = NormalizePath(child);
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
