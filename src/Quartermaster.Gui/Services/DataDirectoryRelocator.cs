namespace Quartermaster.Gui.Services;

public static class DataDirectoryRelocator
{
    public static string Move(string source, string destination, string pointerFile)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, PathComparison)) throw new InvalidOperationException("Choose a different folder.");
        if (IsWithin(source, destination) || IsWithin(destination, source))
            throw new InvalidOperationException("The new folder cannot be inside or contain the current data folder.");
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("Choose an empty folder for the Quartermaster data.");
        Directory.CreateDirectory(destination);
        try
        {
            CopyDirectory(source, destination, source);
            Directory.CreateDirectory(Path.GetDirectoryName(pointerFile)!);
            var temporary = pointerFile + ".tmp";
            File.WriteAllText(temporary, destination);
            File.Move(temporary, pointerFile, overwrite: true);
            return destination;
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true); } catch { }
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination, string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"Cannot move linked data: {entry}");
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(entry, target, root);
            }
            else if (Path.GetFileName(entry).Equals("app.lock", StringComparison.OrdinalIgnoreCase))
            {
                // The new process must acquire a fresh instance lock.
            }
            else File.Copy(entry, target, overwrite: false);
        }
    }

    private static bool IsWithin(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
