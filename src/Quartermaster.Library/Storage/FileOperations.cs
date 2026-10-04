using Quartermaster.Core.Patching;
using Quartermaster.Library.Mods;
using global::System.Security.Cryptography;

namespace Quartermaster.Library.Storage;

/// <summary>Paths for application-owned transitional content, separate from the permanent mod library.</summary>
public static class TemporaryStorage
{
    public static string DirectoryFor(string applicationDirectory) => ManagedPaths.Resolve(applicationDirectory, "temp");
    public static string PathFor(string applicationDirectory, string relativeName)
    {
        var root = DirectoryFor(applicationDirectory);
        Directory.CreateDirectory(root);
        return ManagedPaths.Resolve(root, relativeName);
    }
}

internal static class ManagedPaths
{
    public static string CanonicalDirectory(string path)
    {
        path = Path.GetFullPath(path);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null)
                current = CanonicalDirectory(info.ResolveLinkTarget(returnFinalTarget: true)!.FullName);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }
    public static string Resolve(string root, string relative)
    {
        if (!PatchValidation.IsRelativePath(relative)) throw new InvalidDataException("Unsafe relative file path.");
        root = Path.GetFullPath(root);
        CheckLink(root);
        var current = root;
        foreach (var part in relative.Split('/')) { current = Path.Combine(current, part); CheckLink(current); }
        return current;
    }
    public static void CheckLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Symbolic links are not allowed in managed content.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
    public static IEnumerable<string> Enumerate(string directory)
    {
        CheckLink(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            CheckLink(entry);
            if (Directory.Exists(entry)) { foreach (var file in Enumerate(entry)) yield return file; }
            else yield return entry;
        }
    }
}

internal static class FileIntegrity
{
    public static async Task<string> HashAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    public static async Task VerifyAsync(string path, long size, string hash, CancellationToken ct = default)
    {
        ManagedPaths.CheckLink(path);
        if (!File.Exists(path) || new FileInfo(path).Length != size || !string.Equals(await HashAsync(path, ct).ConfigureAwait(false), hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"File is missing or has changed: {path}");
    }
}
