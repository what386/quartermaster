using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Quartermaster.Providers.Clients.NexusMods;

public sealed class NexusApiException(HttpStatusCode status, TimeSpan? retryAfter = null) : Exception(status switch
{
    HttpStatusCode.Unauthorized => "Nexus rejected the API key. Update it in Settings.",
    HttpStatusCode.Forbidden => "Nexus denied this request. Free downloads need a fresh Download with manager link.",
    HttpStatusCode.NotFound => "The Nexus mod or file is unavailable.",
    HttpStatusCode.TooManyRequests => "Nexus API rate limit reached. Try again later.",
    _ => $"Nexus API request failed ({(int)status})."
})
{
    public HttpStatusCode Status { get; } = status;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed partial class NexusClient : IDisposable
{
    private readonly Func<CancellationToken, Task<string?>> getKey;
    private readonly HttpClient api;
    private readonly HttpClient downloads;
    private readonly bool ownsApi;
    private readonly bool ownsDownloads;
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    public NexusClient(Func<CancellationToken, Task<string?>> getKey, HttpClient? api = null, HttpClient? downloads = null)
    {
        this.getKey = getKey;
        // Never forward API-key headers through HTTP redirects.
        ownsApi = api is null; this.api = api ?? new(new HttpClientHandler { AllowAutoRedirect = false });
        ownsDownloads = downloads is null; this.downloads = downloads ?? new() { Timeout = TimeSpan.FromMinutes(30) };
    }
    public Task<NexusUser> ValidateAsync(CancellationToken ct = default) => GetAsync<NexusUser>("users/validate.json", ct);
    public Task<NexusMod> GetModAsync(long modId, CancellationToken ct = default) => GetAsync<NexusMod>($"games/{NexusLink.Game}/mods/{Positive(modId)}.json", ct);
    public Task<NexusFiles> GetFilesAsync(long modId, CancellationToken ct = default) => GetAsync<NexusFiles>($"games/{NexusLink.Game}/mods/{Positive(modId)}/files.json", ct);
    public async Task<bool> MatchesHashAsync(long modId, long fileId, string md5, CancellationToken ct = default)
    {
        if (md5.Length != 32 || !md5.All(char.IsAsciiHexDigit)) throw new ArgumentException("Invalid file hash.");
        try
        {
            var matches = await GetAsync<NexusHashMatch[]>($"games/{NexusLink.Game}/mods/md5_search/{md5.ToLowerInvariant()}.json", ct);
            return matches.Any(match => match.Mod.Id == modId && match.File.Id == fileId);
        }
        catch (NexusApiException ex) when (ex.Status is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) { return false; }
    }
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || offset < 0) throw new ArgumentException("Enter a search term and a valid offset.");
        const string document = "query($filter: ModsFilter!, $offset: Int!) { mods(filter: $filter, offset: $offset, count: 20) { nodes { modId name summary version thumbnailUrl pictureUrl gameId } } }";
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
        {
            Content = JsonContent.Create(new
            {
                query = document,
                variables = new
                { filter = new { gameDomainName = new[] { new { value = NexusLink.Game, op = "EQUALS" } }, nameStemmed = new[] { new { value = query.Trim(), op = "MATCHES" } } }, offset }
            })
        };
        using var response = await SendAsync(request, ct);
        using var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (result.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0) throw new InvalidDataException("Nexus search failed.");
        var nodes = result.RootElement.GetProperty("data").GetProperty("mods").GetProperty("nodes");
        var scans = await SearchScanResultsAsync(nodes, ct);
        return nodes.EnumerateArray()
            .Select(node => new SearchResult(node.GetProperty("modId").GetInt64().ToString(), node.GetProperty("name").GetString()!,
                node.GetProperty("summary").GetString()!, node.GetProperty("version").GetString()!,
                new Uri($"https://www.nexusmods.com/{NexusLink.Game}/mods/{node.GetProperty("modId").GetInt64()}"),
                ImageUrl(node, "thumbnailUrl") ?? ImageUrl(node, "pictureUrl"))
            { VirusScanStatus = scans.GetValueOrDefault(node.GetProperty("modId").GetInt64().ToString(), "UNKNOWN") }).ToArray();
    }
    private static Uri? ImageUrl(JsonElement node, string field) => node.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
        uri is { Scheme: "https", UserInfo: "" } ? uri : null;
    public async Task DownloadAsync(NexusLink link, long fileId, string destination, CancellationToken ct = default)
    {
        if (link.FileId is { } selected && selected != fileId) throw new ArgumentException("Download grant belongs to a different file.");
        var user = await ValidateAsync(ct);
        if (link.UserId is { } userId && userId != user.Id) throw new InvalidOperationException("The nxm link belongs to a different Nexus account.");
        if (!user.IsPremium && (link.Key is null || link.Expires is null)) throw new InvalidOperationException("Click Download with manager on Nexus, or download in your browser and let Quartermaster scan the file.");
        if (link.Expires is { } expiry && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) throw new InvalidOperationException("This nxm link has expired. Click Download with manager again.");
        var grant = link.Key is null ? "" : $"?key={Uri.EscapeDataString(link.Key)}&expires={link.Expires}";
        var links = await GetAsync<NexusDownload[]>($"games/{NexusLink.Game}/mods/{Positive(link.ModId)}/files/{Positive(fileId)}/download_link.json{grant}", ct);
        var uri = links.Select(item => Uri.TryCreate(item.Uri, UriKind.Absolute, out var value) ? value : null)
            .FirstOrDefault(value => value is { Scheme: "https", UserInfo: "" });
        if (uri is null) throw new InvalidDataException("Nexus returned no valid download URL.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            // A separate client ensures API headers are never sent to download/CDN hosts.
            using var response = await downloads.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new IOException("Nexus file download failed.");
            if (response.Content.Headers.ContentType?.MediaType == "text/html") throw new InvalidDataException("Nexus returned a page instead of an archive.");
            const long maximum = 8L * 1024 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("Download exceeds the size limit.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long size = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += count; if (size > maximum) throw new InvalidDataException("Download exceeds the size limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                if (size == 0 || response.Content.Headers.ContentLength is { } length && length != size) throw new IOException("Download is incomplete.");
            }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, destination, true);
        }
        catch (HttpRequestException) { throw new IOException("Nexus file download failed. Try a fresh download link."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task<T> GetAsync<T>(string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v1/" + route);
        using var response = await SendAsync(request, ct);
        try { return await response.Content.ReadFromJsonAsync<T>(json, ct) ?? throw new InvalidDataException("Empty Nexus API response."); }
        catch (JsonException) { throw new InvalidDataException("Invalid Nexus API response."); }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = await getKey(ct);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Add your personal Nexus API key in Settings.");
        if (key.Length > 4096 || key.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid Nexus API key.");
        request.Headers.Add("apikey", key);
        request.Headers.Add("Application-Name", "Quartermaster"); request.Headers.Add("Application-Version", "0.1.0");
        request.Headers.Accept.ParseAdd("application/json");
        HttpResponseMessage response;
        try { response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new IOException("Cannot reach the Nexus API."); }
        if (response.IsSuccessStatusCode) return response;
        var status = response.StatusCode;
        var retry = response.Headers.RetryAfter?.Delta;
        response.Dispose(); throw new NexusApiException(status, retry);
    }
    private static long Positive(long id) => id > 0 ? id : throw new ArgumentException("Nexus IDs must be positive.");
    public void Dispose() { if (ownsApi) api.Dispose(); if (ownsDownloads) downloads.Dispose(); }
}
