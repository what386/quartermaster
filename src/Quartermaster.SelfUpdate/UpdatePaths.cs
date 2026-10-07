namespace Quartermaster.SelfUpdate;

internal static class UpdatePaths
{
    // Resolve existing root aliases before checking overlap.
    public static string ResolveRoot(string path)
    {
        var absolute = Path.GetFullPath(path);
        var root = Path.GetPathRoot(absolute)!;
        var current = root;
        foreach (var part in absolute[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current)) continue;
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("An update directory contains an unsupported link.");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static void ValidateRoots(SelfUpdateOptions options)
    {
        if (!Directory.Exists(options.InstallDirectory)) throw new DirectoryNotFoundException("The portable installation directory does not exist.");
        CheckLinks(options.InstallDirectory); CheckLinks(options.WorkDirectory);
        if (Within(options.WorkDirectory, options.InstallDirectory) || Within(options.InstallDirectory, options.WorkDirectory))
            throw new ArgumentException("The update work directory must be separate from the installation directory.");
    }
    private static bool Within(string child, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return child.Equals(parent, comparison) || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, comparison);
    }
    public static void CheckLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("An update path is a symbolic link or reparse point.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public static void RequireRegularFile(string path)
    {
        CheckLinks(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device)) != 0)
            throw new FileNotFoundException("A required portable application file is missing or is not a regular file.", path);
    }
}
