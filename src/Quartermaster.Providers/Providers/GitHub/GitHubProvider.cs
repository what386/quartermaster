using System.Net;
using System.Text.Json;
using System.Security.Cryptography;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers.Providers.GitHub;

/// <summary>Public GitHub releases and their uploaded ZIP assets; source archives are excluded.</summary>
public sealed class GitHubProvider : IModProvider, IDisposable
{
    public const string ProviderId = "github";
    public string Id => ProviderId;
    public string DisplayName => "GitHub";
    public bool SupportsSearch => false;
    public bool DownloadsDirectly => true;
    private readonly HttpClient api;
    private readonly HttpClient downloads;
    private readonly bool ownsApi;
    private readonly bool ownsDownloads;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private sealed record Asset(long Id, string Name, string BrowserDownloadUrl, long Size, string? Digest, DateTimeOffset? CreatedAt);
    private sealed record Release(string TagName, string? Name, string? Body, bool Draft, bool Prerelease, DateTimeOffset? PublishedAt, Asset[] Assets);

    public GitHubProvider(HttpClient? api = null, HttpClient? downloads = null)
    {
        this.api = api ?? new HttpClient(); ownsApi = api is null;
        this.downloads = downloads ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; ownsDownloads = downloads is null;
    }
    public bool CanHandle(Uri link) => link.IsAbsoluteUri && link.Scheme == "https" && link.Host is "github.com" or "www.github.com";
    public async Task<ProviderMod> ResolveAsync(string value, CancellationToken ct = default)
    {
        var link = GitHubLink.Parse(value);
        var release = await GetReleaseAsync(link, ct).ConfigureAwait(false);
        var files = Files(link.Repository, release).Where(file => link.AssetName is null || file.FileName == link.AssetName).ToArray();
        if (files.Length == 0) throw new InvalidOperationException("This GitHub release has no matching uploaded ZIP assets.");
        return new(link.Repository, link.Repository.Split('/')[1], release.Body ?? "", release.TagName, link.Page, files)
        { DownloadsDirectly = true };
    }
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default) =>
        throw new NotSupportedException("Add GitHub mods using a repository or release link.");

    public async Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default)
    {
        if (source.Provider != Id || !long.TryParse(source.FileId, out var installed) || installed <= 0)
            throw new ArgumentException("Invalid GitHub source.");
        var link = GitHubLink.Parse("https://github.com/" + source.ModId);
        if (link.Repository != source.ModId || link.Tag is not null) throw new ArgumentException("Invalid GitHub repository identity.");
        var release = await GetReleaseAsync(link, ct).ConfigureAwait(false);
        var files = Files(link.Repository, release);
        if (files.Any(file => file.FileId == source.FileId)) return new(UpdateStatus.Current);
        Asset previous;
        try { previous = await GetAsync<Asset>($"repos/{link.Repository}/releases/assets/{installed}", ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        { return new(UpdateStatus.Unknown, Reason: "The installed GitHub asset was removed. Choose its replacement from the repository."); }
        // A pinned newer release/prerelease must not be downgraded to the latest stable release.
        if (source.InstalledVersion == release.TagName || previous.CreatedAt >= release.PublishedAt) return new(UpdateStatus.Current);
        var candidates = files.Where(file => file.FileName == previous.Name).ToArray();
        if (candidates.Length == 0 && files.Count == 1) candidates = [files[0]];
        return candidates.Length == 1 ? new(UpdateStatus.Available, candidates[0]) :
            new(UpdateStatus.Unknown, Reason: "Cannot identify a unique replacement ZIP in the latest GitHub release.");
    }
    private async Task<Release> GetReleaseAsync(GitHubLink link, CancellationToken ct)
    {
        var endpoint = link.Tag is null ? "latest" : "tags/" + Uri.EscapeDataString(link.Tag);
        var release = await GetAsync<Release>($"repos/{link.Repository}/releases/{endpoint}", ct).ConfigureAwait(false);
        if (release.Draft || link.Tag is null && release.Prerelease) throw new InvalidOperationException("No published stable GitHub release is available.");
        if (string.IsNullOrWhiteSpace(release.TagName) || release.Assets is null) throw new InvalidDataException("Invalid GitHub release response.");
        return release;
    }
    private static IReadOnlyList<ProviderFile> Files(string repository, Release release) => release.Assets
        .Where(asset => asset.Id > 0 && asset.Size >= 0 && asset.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
        .Select(asset =>
        {
            var link = GitHubLink.Parse(asset.BrowserDownloadUrl);
            if (link.Repository != repository || link.Tag != release.TagName || link.AssetName != asset.Name)
                throw new InvalidDataException("GitHub asset URL does not match its repository or release.");
            var sha = asset.Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? asset.Digest[7..] : null;
            if (sha is not null && (sha.Length != 64 || !sha.All(Uri.IsHexDigit))) throw new InvalidDataException("Invalid GitHub asset digest.");
            return new ProviderFile(ProviderId, repository, asset.Id.ToString(), asset.Name, asset.Name, release.TagName,
                new Uri(asset.BrowserDownloadUrl), Size: asset.Size, Sha256: sha);
        }).ToArray();

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
        request.Headers.UserAgent.ParseAdd("Quartermaster");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await api.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub refused the request or its public API rate limit was reached. Try again later.", null, response.StatusCode);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("GitHub repository, release, or asset was not found. Use a public repository with a published release.", null, response.StatusCode);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Empty GitHub response.");
    }
    public DownloadScanner CreateScanner() => new GitHubDownloadScanner();
    public async Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default)
    {
        var parsed = GitHubLink.Parse(link);
        if (file.Provider != Id || parsed.Repository != file.ModId || parsed.Tag != file.Version || parsed.AssetName != file.FileName ||
            link != file.DownloadPage.AbsoluteUri) throw new ArgumentException("Download belongs to another GitHub asset.");
        const long maxBytes = 8L * 1024 * 1024 * 1024;
        if (file.Size is < 0 or > maxBytes) throw new InvalidDataException("GitHub archive exceeds the size limit.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.UserAgent.ParseAdd("Quartermaster");
            using var response = await downloads.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && (length > maxBytes || file.Size is { } size && length != size))
                throw new InvalidDataException("GitHub download size does not match the requested asset.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += count;
                    if (total > maxBytes || file.Size is { } expected && total > expected) throw new InvalidDataException("GitHub download exceeds the expected size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                if (file.Size is { } expectedSize && total != expectedSize) throw new InvalidDataException("GitHub download is incomplete.");
            }
            if (file.Sha256 is { } sha && !sha.Equals(Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GitHub download checksum does not match the release asset.");
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { if (ownsApi) api.Dispose(); if (ownsDownloads) downloads.Dispose(); }
    private sealed class GitHubDownloadScanner() : DownloadScanner;
}
