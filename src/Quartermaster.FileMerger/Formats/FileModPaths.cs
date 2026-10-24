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

    /// <summary>HD2FM1's length and 32-byte samples from the start, midpoint and end. Not a cryptographic hash.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 64) throw new InvalidDataException("A sealed game config is too short.");
        return bytes.Length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(bytes[..32]) +
            Convert.ToHexString(bytes.Slice(bytes.Length / 2, 32)) + Convert.ToHexString(bytes[^32..]);
    }
}
