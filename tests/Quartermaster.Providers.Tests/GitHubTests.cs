using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Tests;
using Quartermaster.Providers.Downloads;
using Quartermaster.Providers.Providers.GitHub;
using Xunit;

namespace Quartermaster.Providers.Tests;

public sealed class GitHubTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request)); }
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static object Asset(long id = 20, string name = "mod.zip", string tag = "v2", byte[]? bytes = null) => new
    {
        id, name, browser_download_url = $"https://github.com/owner/mod/releases/download/{tag}/{name}",
        size = bytes?.Length ?? 3, digest = bytes is null ? null : "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
        created_at = id == 10 ? "2026-08-01T00:00:00Z" : "2026-09-01T00:00:00Z"
    };
    private static object Release(params object[] assets) => new
    { tag_name = "v2", name = "Latest", body = "Reticle mod", draft = false, prerelease = false, published_at = "2026-09-01T00:00:00Z", assets };

    [Theory]
    [InlineData("https://github.com/owner/mod", null, null)]
    [InlineData("https://github.com/owner/mod.git/", null, null)]
    [InlineData("https://www.github.com/owner/mod/releases/latest", null, null)]
    [InlineData("https://github.com/owner/mod/releases/tag/v2", "v2", null)]
    [InlineData("https://github.com/owner/mod/releases/download/v2/mod.zip", "v2", "mod.zip")]
    [InlineData("https://github.com/owner/mod/releases/tag/release%2Fv2", "release/v2", null)]
    public void ParsesRepositoryAndReleaseLinks(string input, string? tag, string? asset)
    { var parsed = GitHubLink.Parse(input); Assert.Equal("owner/mod", parsed.Repository); Assert.Equal(tag, parsed.Tag); Assert.Equal(asset, parsed.AssetName); }

    [Theory]
    [InlineData("http://github.com/owner/mod")]
    [InlineData("https://github.com.evil.test/owner/mod")]
    [InlineData("https://user@github.com/owner/mod")]
    [InlineData("https://github.com:123/owner/mod")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/mod/tree/main")]
    [InlineData("https://github.com/owner%2Fother/mod")]
    public void RejectsUnsupportedLinks(string input) => Assert.Throws<ArgumentException>(() => GitHubLink.Parse(input));

    [Fact]
    public async Task ResolvesUploadedZipAssetsAndUsesPublicApiHeaders()
    {
        using var api = Http(request =>
        {
            Assert.Equal("/repos/owner/mod/releases/latest", request.RequestUri!.AbsolutePath);
            Assert.NotEmpty(request.Headers.UserAgent); Assert.Null(request.Headers.Authorization);
            Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
            return Json(Release(Asset(), Asset(21, "alternate.ZIP"), Asset(22, "source.tar.gz")));
        });
        using var provider = new GitHubProvider(api);
        var mod = await provider.ResolveAsync("https://github.com/owner/mod");
        Assert.Equal(2, mod.Files.Count); Assert.True(mod.DownloadsDirectly); Assert.Equal("Download", mod.DownloadAction);
        Assert.Equal("v2", mod.Version); Assert.Equal("owner/mod", mod.ModId); Assert.All(mod.Files, file => Assert.Equal("github", file.Provider));
    }

    [Fact]
    public async Task TaggedAssetLinkResolvesOnlyTheRequestedZip()
    {
        using var api = Http(request =>
        {
            Assert.Equal("/repos/owner/mod/releases/tags/v2", request.RequestUri!.AbsolutePath);
            return Json(Release(Asset(), Asset(21, "other.zip")));
        });
        using var provider = new GitHubProvider(api);
        Assert.Equal("21", Assert.Single((await provider.ResolveAsync("https://github.com/owner/mod/releases/download/v2/other.zip")).Files).FileId);
    }

    [Fact]
    public async Task EmptyAssetListAndSpoofedDownloadUrlsAreReported()
    {
        using var empty = Http(_ => Json(Release())); using var provider = new GitHubProvider(empty);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveAsync("https://github.com/owner/mod"));
        using var spoofed = Http(_ => Json(Release(new { id = 20, name = "mod.zip", size = 3, browser_download_url = "https://evil.test/mod.zip" })));
        using var other = new GitHubProvider(spoofed);
        await Assert.ThrowsAsync<ArgumentException>(() => other.ResolveAsync("https://github.com/owner/mod"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.Forbidden, "rate limit")]
    public async Task ApiFailuresHaveActionableMessages(HttpStatusCode status, string message)
    {
        using var api = Http(_ => new(status)); using var provider = new GitHubProvider(api);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResolveAsync("https://github.com/owner/mod"));
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("mod.zip", "mod.zip", true)]
    [InlineData("old-name.zip", "new-name.zip", false)]
    public async Task UpdatesMatchAssetNameAndAvoidAmbiguousVariants(string oldName, string newName, bool available)
    {
        using var api = Http(request => request.RequestUri!.AbsolutePath.EndsWith("/assets/10")
            ? Json(Asset(10, oldName, "v1")) : Json(Release(Asset(20, newName), Asset(21, "other.zip"))));
        using var provider = new GitHubProvider(api);
        var update = await provider.CheckUpdateAsync(new("github", "owner/mod", "10", "v1"));
        Assert.Equal(available ? Quartermaster.Providers.Providers.UpdateStatus.Available : Quartermaster.Providers.Providers.UpdateStatus.Unknown, update.Status);
        if (available) Assert.Equal("20", update.File!.FileId);
        else Assert.NotNull(update.Reason);
        Assert.Equal(Quartermaster.Providers.Providers.UpdateStatus.Current, (await provider.CheckUpdateAsync(new("github", "owner/mod", "20", "v2"))).Status);
    }

    [Fact]
    public async Task SingleRenamedZipCanReplaceAnOlderAsset()
    {
        using var api = Http(request => request.RequestUri!.AbsolutePath.EndsWith("/assets/10") ? Json(Asset(10, "v1.zip", "v1")) : Json(Release(Asset(20, "v2.zip"))));
        using var provider = new GitHubProvider(api);
        Assert.Equal("20", (await provider.CheckUpdateAsync(new("github", "owner/mod", "10", "v1"))).File!.FileId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadsVerifyDigestAndCleanTemporaryFiles(bool corrupt)
    {
        using var f = new Fixture(); byte[] bytes = [1, 2, 3];
        using var api = Http(_ => Json(Release(Asset(bytes: bytes))));
        using var downloads = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(corrupt ? [3, 2, 1] : bytes) });
        using var provider = new GitHubProvider(api, downloads); var file = Assert.Single((await provider.ResolveAsync("https://github.com/owner/mod")).Files);
        var path = Path.Combine(f.App, "temp", "downloads", "mod.zip");
        if (corrupt) { await Assert.ThrowsAsync<InvalidDataException>(() => provider.DownloadAsync(file.DownloadPage.AbsoluteUri, file, path)); Assert.False(File.Exists(path)); }
        else { await provider.DownloadAsync(file.DownloadPage.AbsoluteUri, file, path); Assert.Equal(bytes, File.ReadAllBytes(path)); }
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task QueueImportsDirectlyWithoutBrowserOrWatchedFoldersAndTracksSource()
    {
        using var f = new Fixture(); var bytes = await File.ReadAllBytesAsync(f.Zip(f.Source("GitHub mod")));
        using var api = Http(_ => Json(Release(Asset(bytes: bytes))));
        using var downloads = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var provider = new GitHubProvider(api, downloads);
        await using var manager = new ProviderManager(f.Library, new(f.App), [provider], Path.Combine(f.App, "temp", "downloads"), _ => throw new Exception("Browser should not open"));
        await manager.InitializeAsync();
        var profile = ProfileEditor.Create("Profile"); await f.Library.SaveProfileAsync(profile);
        var file = Assert.Single((await manager.ResolveAsync("https://github.com/owner/mod")).Files);
        var job = await manager.QueueAsync(file, profile.Id);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var state = await f.Library.LoadAsync(); var mod = Assert.Single(state.Mods);
        Assert.Equal(new SourceReference("github", "owner/mod", "20", "v2"), Assert.Single(mod.Sources));
        Assert.Equal(mod.Id, Assert.Single(Assert.Single(state.Profiles).Entries).ModId);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.App, "temp", "downloads")));
    }
}
