namespace Quartermaster.Library.Profiles;

/// <summary>Portable profile artwork is embedded, so backups never depend on external image paths.</summary>
public static class ProfileAppearance
{
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length != 7 || value[0] != '#' || !value.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("Enter a color as #RRGGBB.");
        return value.ToUpperInvariant();
    }

    public static async Task<string> ReadThumbnailAsync(string path, CancellationToken ct = default)
    {
        await using var input = File.OpenRead(path);
        if (input.Length > MaxImageBytes) throw new ArgumentException("Profile images must be 2 MB or smaller.");
        var bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        ValidateImage(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static void ValidateThumbnail(string? value)
    {
        if (value is null) return;
        if (value.Length > (MaxImageBytes + 2) / 3 * 4) throw new ArgumentException("Profile image exceeds the size limit.");
        try { ValidateImage(Convert.FromBase64String(value)); }
        catch (FormatException ex) { throw new ArgumentException("Invalid profile image.", ex); }
    }

    private static void ValidateImage(ReadOnlySpan<byte> bytes)
    {
        var png = bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var jpeg = bytes.StartsWith(new byte[] { 255, 216, 255 });
        var webp = bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8);
        if (bytes.Length > MaxImageBytes || !(png || jpeg || webp)) throw new ArgumentException("Choose a PNG, JPEG, or WebP profile image (2 MB maximum).");
    }
}
