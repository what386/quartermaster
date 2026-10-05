using System.Globalization;
using System.Text.Json.Serialization;

namespace Quartermaster.Providers.Clients.NexusMods;

public sealed record NexusUser([property: JsonPropertyName("user_id")] long Id, string Name,
    [property: JsonPropertyName("is_premium")] bool IsPremium);
public sealed record NexusMod([property: JsonPropertyName("mod_id")] long Id, string Name, string Summary, string Version, bool Available);
public sealed record NexusFile([property: JsonPropertyName("file_id")] long Id, string Name,
    [property: JsonPropertyName("file_name")] string FileName, string Version,
    [property: JsonPropertyName("category_id")] int CategoryId,
    [property: JsonPropertyName("is_primary")] bool IsPrimary,
    [property: JsonPropertyName("size_in_bytes")] long? Size = null)
{
    [JsonIgnore] public bool IsAvailable => CategoryId is 1 or 2 or 3 or 5;
}
public sealed record NexusFileUpdate([property: JsonPropertyName("old_file_id")] long OldId,
    [property: JsonPropertyName("new_file_id")] long NewId);
public sealed record NexusFiles(IReadOnlyList<NexusFile> Files, [property: JsonPropertyName("file_updates")] IReadOnlyList<NexusFileUpdate> Updates);
internal sealed record NexusDownload(string Uri);
internal sealed record NexusHashMatch(NexusMod Mod, [property: JsonPropertyName("file_details")] NexusFile File);

/// <summary>Only HD2 URLs are accepted. Signed download grants are never included in ToString().</summary>
public sealed class NexusLink
{
    public const string Game = "helldivers2";
    public long ModId { get; private init; }
    public long? FileId { get; private init; }
    public string? Key { get; private init; }
    public long? Expires { get; private init; }
    public long? UserId { get; private init; }
    public bool IsNxm { get; private init; }
    public Uri Page => new($"https://www.nexusmods.com/{Game}/mods/{ModId.ToString(CultureInfo.InvariantCulture)}" +
        (FileId is { } file ? $"?tab=files&file_id={file.ToString(CultureInfo.InvariantCulture)}" : ""));
    public override string ToString() => Page.AbsoluteUri;
    public static NexusLink Parse(string value)
    {
        if (value.Length > 8192 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo != "" || !uri.IsDefaultPort)
            throw new ArgumentException("Invalid Nexus link.");
        var nxm = uri.Scheme == "nxm";
        if (nxm ? uri.Host != Game : uri.Scheme != "https" || (uri.Host != "www.nexusmods.com" && uri.Host != "nexusmods.com"))
            throw new ArgumentException("Use a Helldivers 2 Nexus Mods link.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (!nxm && parts.Length >= 4 && parts[0] == "games") parts = parts[1..];
        if (nxm)
        {
            if (parts.Length != 4 || parts[0] != "mods" || parts[2] != "files") throw new ArgumentException("Invalid nxm link.");
        }
        else if (parts.Length != 3 || parts[0] != Game || parts[1] != "mods") throw new ArgumentException("Use a Helldivers 2 mod page.");
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var item = pair.Split('=', 2);
            if (!query.TryAdd(Uri.UnescapeDataString(item[0]), item.Length > 1 ? Uri.UnescapeDataString(item[1]) : "")) throw new ArgumentException("Duplicate Nexus URL parameter.");
        }
        long Id(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id : throw new ArgumentException("Invalid Nexus mod or file ID.");
        var fileId = nxm ? Id(parts[3]) : query.TryGetValue("file_id", out var file) ? Id(file) : (long?)null;
        query.TryGetValue("key", out var key);
        var expires = query.TryGetValue("expires", out var expiry) ? Id(expiry) : (long?)null;
        var user = query.TryGetValue("user_id", out var userId) ? Id(userId) : (long?)null;
        if (nxm && ((key is null) != (expires is null) || key == "")) throw new ArgumentException("Incomplete nxm download grant.");
        return new() { ModId = Id(parts[nxm ? 1 : 2]), FileId = fileId, Key = nxm ? key : null, Expires = nxm ? expires : null, UserId = nxm ? user : null, IsNxm = nxm };
    }
}
