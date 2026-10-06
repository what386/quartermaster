using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers.Clients.AyakaMods;

/// <summary>AyakaMods' XenForo API: game-filtered browsing and authenticated hosted ZIP downloads.</summary>
public sealed class AyakaProvider : IModProvider, IDisposable
{
    public const string ProviderId = "ayakamods";
    private const string ApiRoot = "https://ayakamods.com/api/";
    private const long MaxBytes = 8L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient api;
    private readonly HttpClient downloads;
    private readonly bool ownsApi;
    private readonly bool ownsDownloads;
    private readonly Func<CancellationToken, Task<string?>> key;

    public string Id => ProviderId;
    public string DisplayName => "AyakaMods";
    public bool DownloadsDirectly => true;

    public AyakaProvider(HttpClient? api = null, HttpClient? downloads = null,
        Func<CancellationToken, Task<string?>>? key = null)
    {
        // Custom headers survive automatic redirects in HttpClient. Follow download
        // redirects ourselves so XF-Api-Key can never reach another host.
        this.api = api ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.downloads = downloads ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromMinutes(30) };
        ownsApi = api is null; ownsDownloads = downloads is null;
        this.key = key ?? (_ => Task.FromResult(BuildKey));
    }

    private static readonly string? BuildKey = typeof(AyakaProvider).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "AyakaModsApiKey")?.Value;

    public bool CanHandle(Uri link) => IsPublicHttps(link) && link.Host is "ayakamods.com" or "www.ayakamods.com";

    private static bool IsPublicHttps(Uri link) => link.IsAbsoluteUri && link.Scheme == "https"
        && link.IsDefaultPort && link.UserInfo.Length == 0;

    private static long PositiveId(string? value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("Invalid AyakaMods ID.");
        return id;
    }

    private long ParseModLink(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var link) || !CanHandle(link))
            throw new ArgumentException("Enter an AyakaMods mod page link.");
        var parts = link.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3 || parts[0] != "mods" || parts.Length == 3 && parts[2] != "download")
            throw new ArgumentException("Enter an AyakaMods mod page link.");
        return PositiveId(parts[1][(parts[1].LastIndexOf('.') + 1)..]);
    }

    private async Task<HashSet<long>> GetGameIdsAsync(CancellationToken ct)
    {
        var games = await ReadPagesAsync("mod-games/", async (path, token) =>
        {
            var result = await GetAsync<AyakaGames>(path, token).ConfigureAwait(false);
            return (result.Games, result.Pagination);
        }, ct).ConfigureAwait(false);
        var ids = games.Where(game => game.DisplayName.Equals("Helldivers 2", StringComparison.OrdinalIgnoreCase)
            || game.DisplayName.Equals("Helldivers II", StringComparison.OrdinalIgnoreCase)).Select(game => game.GameId).ToHashSet();
        if (ids.Count == 0 || ids.Any(id => id <= 0))
            throw new InvalidOperationException("This build's AyakaMods integration key cannot access Helldivers 2.");
        return ids;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (string.IsNullOrWhiteSpace(query)) return [];
        var ids = await GetGameIdsAsync(ct).ConfigureAwait(false);
        // There is no documented text-search filter. Do not pass game_id either:
        // scoped keys already filter server-side, unrestricted keys need this local filter.
        var mods = await ReadPagesAsync("mods/?order=download_count&direction=desc", async (path, token) =>
        {
            var result = await GetAsync<AyakaModsPage>(path, token).ConfigureAwait(false);
            return (result.Mods, result.Pagination);
        }, ct).ConfigureAwait(false);
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return mods.Where(mod => ids.Contains(mod.GameId) && words.All(word =>
                (mod.Title + " " + mod.TagLine).Contains(word, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(mod => mod.ModId).Skip(offset).Select(mod =>
            {
                ValidateMod(mod, ids);
                Uri? icon = Uri.TryCreate(mod.IconUrl, UriKind.Absolute, out var uri) && IsPublicHttps(uri) ? uri : null;
                return new SearchResult(mod.ModId.ToString(CultureInfo.InvariantCulture), mod.Title,
                    mod.TagLine ?? "", mod.Version ?? "", ModPage(mod), icon);
            }).ToArray();
    }

    public async Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default)
    {
        var id = ParseModLink(link);
        var mod = await GetModAsync(id, ct).ConfigureAwait(false);
        var versions = await GetVersionsAsync(id, ct).ConfigureAwait(false);
        var latest = versions.MaxBy(version => version.VersionId)
            ?? throw new InvalidOperationException("This AyakaMods mod has no published versions.");
        var files = Files(mod, latest);
        if (files.Count == 0) throw new NotSupportedException("This AyakaMods version has no hosted ZIP files. For external downloads, add the source provider's link instead.");
        return new(id.ToString(CultureInfo.InvariantCulture), mod.Title, mod.TagLine ?? "",
            latest.DisplayVersion, ModPage(mod), files)
        { DownloadsDirectly = true };
    }

    private async Task<AyakaMod> GetModAsync(long id, CancellationToken ct)
    {
        var ids = await GetGameIdsAsync(ct).ConfigureAwait(false);
        var result = await GetAsync<AyakaModResponse>($"mods/{id}/", ct).ConfigureAwait(false);
        if (result.Mod is null || result.Mod.ModId != id) throw new InvalidDataException("Invalid AyakaMods mod response.");
        ValidateMod(result.Mod, ids);
        return result.Mod;
    }

    private static void ValidateMod(AyakaMod mod, HashSet<long> games)
    {
        if (mod.ModId <= 0 || string.IsNullOrWhiteSpace(mod.Title)) throw new InvalidDataException("Invalid AyakaMods mod response.");
        if (!games.Contains(mod.GameId)) throw new NotSupportedException("Only Helldivers 2 mods can be imported from AyakaMods.");
    }

    private Uri ModPage(AyakaMod mod)
    {
        var value = mod.ViewUrl ?? $"https://ayakamods.com/mods/{mod.ModId}/";
        if (ParseModLink(value) != mod.ModId) throw new InvalidDataException("AyakaMods mod page belongs to another mod.");
        return new Uri(value);
    }

    private async Task<AyakaVersion[]> GetVersionsAsync(long modId, CancellationToken ct)
    {
        var versions = await ReadPagesAsync($"mods/{modId}/versions/", async (path, token) =>
        {
            var result = await GetAsync<AyakaVersions>(path, token).ConfigureAwait(false);
            return (result.Versions, result.Pagination);
        }, ct).ConfigureAwait(false);
        foreach (var version in versions) ValidateVersion(version, modId);
        return versions;
    }

    private static void ValidateVersion(AyakaVersion version, long modId)
    {
        if (version.VersionId <= 0 || version.ModId != modId) throw new InvalidDataException("AyakaMods version belongs to another mod or has an invalid ID.");
    }

    private static IReadOnlyList<ProviderFile> Files(AyakaMod mod, AyakaVersion version) =>
        mod.ModType == "download_local" ? (version.Files ?? []).Where(file => file.Filename?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
            .Select(file =>
            {
                if (file.Id <= 0 || file.Size is < 0 or > MaxBytes || file.Filename.IndexOfAny(['/', '\\']) >= 0)
                    throw new InvalidDataException("Invalid AyakaMods file metadata.");
                return new ProviderFile(ProviderId, mod.ModId.ToString(CultureInfo.InvariantCulture), $"{version.VersionId}:{file.Id}",
                    file.Filename, file.Filename, version.DisplayVersion,
                    new Uri($"{ApiRoot}mod-versions/{version.VersionId}/download?file={file.Id}"), Size: file.Size);
            }).ToArray() : [];

    public async Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default)
    {
        if (source.Provider != Id) throw new ArgumentException("Invalid AyakaMods source.");
        var modId = PositiveId(source.ModId);
        var (installedVersion, installedFile) = ParseFileId(source.FileId);
        var mod = await GetModAsync(modId, ct).ConfigureAwait(false);
        var versions = await GetVersionsAsync(modId, ct).ConfigureAwait(false);
        var latest = versions.MaxBy(version => version.VersionId);
        if (latest is null) return new(UpdateStatus.Unknown, Reason: "This AyakaMods mod has no published versions.");
        var files = Files(mod, latest);
        if (files.Any(file => file.FileId == source.FileId)) return new(UpdateStatus.Current);
        var previous = versions.SingleOrDefault(version => version.VersionId == installedVersion);
        if (previous is null)
        {
            try
            {
                previous = (await GetAsync<AyakaVersionResponse>($"mod-versions/{installedVersion}/", ct).ConfigureAwait(false)).Version;
                if (previous is null) throw new InvalidDataException("Invalid AyakaMods version response.");
                ValidateVersion(previous, modId);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            { return new(UpdateStatus.Unknown, Reason: "The installed AyakaMods version was removed. Choose its replacement manually."); }
        }
        var old = previous.Files?.SingleOrDefault(file => file.Id == installedFile);
        if (old is null) return new(UpdateStatus.Unknown, Reason: "The installed AyakaMods file was removed. Choose its replacement manually.");
        if (installedVersion > latest.VersionId) return new(UpdateStatus.Current);
        if (installedVersion == latest.VersionId) return new(UpdateStatus.Unknown, Reason: "The installed AyakaMods file is no longer available as a hosted ZIP.");
        var candidates = files.Where(file => file.FileName == old.Filename).ToArray();
        if (candidates.Length == 0 && files.Count == 1) candidates = [files[0]];
        return candidates.Length == 1 ? new(UpdateStatus.Available, candidates[0]) :
            new(UpdateStatus.Unknown, Reason: "Cannot identify a unique replacement ZIP in the latest AyakaMods version.");
    }

    private static (long Version, long File) ParseFileId(string? value)
    {
        var parts = value?.Split(':');
        if (parts?.Length != 2) throw new ArgumentException("Invalid AyakaMods file identity.");
        return (PositiveId(parts[0]), PositiveId(parts[1]));
    }

    private static async Task<T[]> ReadPagesAsync<T>(string path,
        Func<string, CancellationToken, Task<(T[] Items, AyakaPagination? Pagination)>> fetch, CancellationToken ct)
    {
        var items = new List<T>();
        for (var page = 1; ; page++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await fetch(path + (path.Contains('?') ? "&" : "?") + "page=" + page, ct).ConfigureAwait(false);
            if (result.Items is null) throw new InvalidDataException("Invalid AyakaMods list response.");
            items.AddRange(result.Items);
            var pagination = result.Pagination;
            if (pagination is null) return items.ToArray();
            if (pagination.CurrentPage != page || pagination.LastPage < page || pagination.LastPage > 10000
                || pagination.PerPage <= 0 || pagination.Total < 0 || items.Count > pagination.Total)
                throw new InvalidDataException("Invalid AyakaMods pagination.");
            if (page == pagination.LastPage) return items.ToArray();
            if (result.Items.Length == 0) throw new InvalidDataException("AyakaMods returned an empty page before the end of the list.");
        }
    }

    private static string ValidateKey(string value)
    {
        value = value.Trim();
        if (value.Length is 0 or > 4096 || value.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid AyakaMods API key format.");
        return value;
    }

    private async Task<string> GetKeyAsync(CancellationToken ct)
    {
        var value = await key(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("This Quartermaster build does not include an AyakaMods integration key.");
        return ValidateKey(value);
    }

    private static HttpRequestMessage Request(Uri uri, string? credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Quartermaster");
        if (credential is not null) request.Headers.Add("XF-Api-Key", credential);
        return request;
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var credential = await GetKeyAsync(ct).ConfigureAwait(false);
        using var request = Request(new Uri(ApiRoot + path), credential);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        await CheckResponseAsync(response, credential, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try { return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Empty AyakaMods response."); }
        catch (JsonException ex) { throw new InvalidDataException("AyakaMods returned an invalid API response. Its public API may not be available yet.", ex); }
    }

    private static async Task CheckResponseAsync(HttpResponseMessage response, string credential, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "AyakaMods rejected this build's integration key.",
            HttpStatusCode.Forbidden => "AyakaMods refused access. This build's integration key may lack the required game scope or permissions.",
            HttpStatusCode.NotFound => "AyakaMods mod, version, or API endpoint was not found. The public API may not be available yet.",
            HttpStatusCode.TooManyRequests => "AyakaMods API rate limit reached. Try again later.",
            _ => $"AyakaMods API request failed (HTTP {(int)response.StatusCode})."
        };
        try
        {
            var error = await response.Content.ReadFromJsonAsync<AyakaErrors>(Json, ct).ConfigureAwait(false);
            if (error?.Errors is { Length: > 0 })
                reason += " " + string.Join("; ", error.Errors.Select(item => $"{item.Code}: {item.Message}")).Replace(credential, "[redacted]", StringComparison.Ordinal);
        }
        catch (JsonException) { }
        throw new HttpRequestException(reason, null, response.StatusCode);
    }

    public DownloadScanner CreateScanner() => new AyakaDownloadScanner();

    public async Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default)
    {
        if (file.Provider != Id) throw new ArgumentException("Invalid AyakaMods file.");
        var (versionId, fileId) = ParseFileId(file.FileId);
        var endpoint = new Uri($"{ApiRoot}mod-versions/{versionId}/download?file={fileId}");
        if (link != endpoint.AbsoluteUri || file.DownloadPage != endpoint)
            throw new ArgumentException("Download belongs to another AyakaMods file.");
        var mod = await GetModAsync(PositiveId(file.ModId), ct).ConfigureAwait(false);
        var version = (await GetAsync<AyakaVersionResponse>($"mod-versions/{versionId}/", ct).ConfigureAwait(false)).Version;
        if (version is null) throw new InvalidDataException("Invalid AyakaMods version response.");
        ValidateVersion(version, mod.ModId);
        var actual = Files(mod, version).SingleOrDefault(candidate => candidate.FileId == file.FileId);
        if (actual is null || actual.FileName != file.FileName || actual.Size != file.Size)
            throw new InvalidDataException("AyakaMods file metadata changed. Resolve the mod again before downloading.");
        var credential = await GetKeyAsync(ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var response = await DownloadResponseAsync(endpoint, credential, ct).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && (length > MaxBytes || length != file.Size))
                throw new InvalidDataException("AyakaMods download size does not match the requested file.");
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > MaxBytes || total > file.Size) throw new InvalidDataException("AyakaMods download exceeds the expected size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                if (total != file.Size) throw new InvalidDataException("AyakaMods download is incomplete.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<HttpResponseMessage> DownloadResponseAsync(Uri endpoint, string credential, CancellationToken ct)
    {
        var uri = endpoint;
        var authenticated = true;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var request = Request(uri, authenticated ? credential : null);
            var response = await downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            try
            {
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("AyakaMods download redirect has no destination.");
                    var next = new Uri(uri, location);
                    if (!IsPublicHttps(next)) throw new InvalidDataException("AyakaMods returned an unsafe download redirect.");
                    authenticated &= next.Host == endpoint.Host && next.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal);
                    uri = next;
                    response.Dispose();
                    continue;
                }
                await CheckResponseAsync(response, credential, ct).ConfigureAwait(false);
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("AyakaMods download redirected too many times.");
    }

    public void Dispose() { if (ownsApi) api.Dispose(); if (ownsDownloads) downloads.Dispose(); }
    private sealed class AyakaDownloadScanner() : DownloadScanner;
}
