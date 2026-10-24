using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Quartermaster.FileMerger;

public static partial class FileModPaths
{
    [GeneratedRegex(@"^data/game/generated_[a-zA-Z0-9_]+\.dl_bin$", RegexOptions.CultureInvariant)]
    private static partial Regex TargetPattern();

    public static string ValidateTarget(string target)
    {
        target = target.Replace('\\', '/');
        if (!TargetPattern().IsMatch(target)) throw new InvalidDataException($"Unsupported file-mod target: {target}");
        return target.ToLowerInvariant();
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // Game installations and app-data roots are caller-selected locations. Resolve their aliases once;
    // RejectLink still protects descendants used for config, journal and backup writes.
    internal static string ResolveRoot(string path, bool mustExist, int remainingLinks = 40)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (directory.LinkTarget is not null)
            {
                if (remainingLinks == 0) throw new IOException($"Too many directory links in file-mod root: {path}");
                var target = directory.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new IOException($"Could not resolve file-mod root: {current}");
                current = ResolveRoot(target.FullName, mustExist: true, remainingLinks - 1);
            }
            else if (File.Exists(current)) throw new IOException($"File-mod root contains a file instead of a directory: {current}");
            else if (mustExist && !directory.Exists) throw new DirectoryNotFoundException($"File-mod directory does not exist: {current}");
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    /// <summary>HD2FM1's length and 32-byte samples from the start, midpoint and end. Not a cryptographic hash.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64) throw new InvalidDataException("A sealed game config is too short.");
        return bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(bytes[..32]) +
            Convert.ToHexString(bytes.Slice(bytes.Length / 2, 32)) + Convert.ToHexString(bytes[^32..]);
    }
}
