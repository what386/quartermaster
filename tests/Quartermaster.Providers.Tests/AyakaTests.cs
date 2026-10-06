using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Tests;
using Quartermaster.Providers.Clients;
using Quartermaster.Providers.Clients.AyakaMods;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Providers.Tests;

public sealed class AyakaTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(send(request)); }
    }
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static object Games() => new { games = new[] { new { game_id = 3, title = "Helldivers 2" }, new { game_id = 7, title = "Other game" } } };
    private static object Mod(long id = 42, long game = 3, string type = "download_local", string title = "Reticle", string summary = "Custom HUD") => new
    {
        mod_id = id,
        game_id = game,
        mod_type = type,
        title,
        tag_line = summary,
        version = "2.0",
        view_url = $"https://ayakamods.com/mods/reticle.{id}/",
        icon_url = "https://ayakamods.com/icons/reticle.png"
    };
    private static object File(long id = 12, string name = "mod.zip", long size = 3) => new
    {
        id,
        filename = name,
        size,
        // The provider must use the authenticated endpoint, never this raw storage URL.
        download_url = "https://storage.example/raw/mod.zip"
    };
    private static object Version(long id = 20, long mod = 42, params object[] files) => new
    { version_id = id, mod_id = mod, version_string = "v" + id, files };
    private static object Pagination(int page, int last, int total) => new { current_page = page, last_page = last, per_page = 1, total };
    private static AyakaProvider Provider(HttpClient api, HttpClient? downloads = null) => new(api, downloads, _ => Task.FromResult<string?>("test-key"));
    private static HttpResponseMessage Api(HttpRequestMessage request, params object[] versions) => request.RequestUri!.AbsolutePath switch
    {
        "/api/mod-games/" => Json(Games()),
        "/api/mods/42/" => Json(new { mod = Mod() }),
        "/api/mods/42/versions/" => Json(new { versions }),
        "/api/mod-versions/20/" => Json(new { version = Version(20, 42, File()) }),
        _ => throw new InvalidOperationException("Unexpected API request: " + request.RequestUri)
    };

    [Fact]
    public async Task DefaultProviderUsesTheBuildKeyWithoutAUserCredentialStore()
    {
        var embedded = typeof(AyakaProvider).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "AyakaModsApiKey").Value;
        var requests = 0;
        using var api = Http(request =>
        {
            requests++;
            Assert.Equal(embedded, Assert.Single(request.Headers.GetValues("XF-Api-Key")));
            return Api(request, Version(20, 42, File()));
        });
        using var provider = new AyakaProvider(api);
        if (string.IsNullOrEmpty(embedded))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveAsync("https://ayakamods.com/mods/reticle.42/"));
            Assert.Contains("integration key", error.Message); Assert.Equal(0, requests);
        }
        else
        {
            Assert.Single((await provider.ResolveAsync("https://ayakamods.com/mods/reticle.42/")).Files);
            Assert.Equal(3, requests);
        }
    }

    [Fact]
    public async Task SearchReadsEveryPageFiltersGamesAndMatchesTitleAndSummary()
    {
        var pages = new List<int>();
        using var api = Http(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("XF-Api-Key")));
            if (request.RequestUri!.AbsolutePath == "/api/mod-games/") return Json(Games());
            Assert.Equal("/api/mods/", request.RequestUri.AbsolutePath);
            Assert.DoesNotContain("game_id", request.RequestUri.Query);
            var page = request.RequestUri.Query.Contains("page=1") ? 1 : 2; pages.Add(page);
            return Json(new { mods = page == 1 ? new[] { Mod(1, 7, title: "Reticle") } : new[] { Mod() }, pagination = Pagination(page, 2, 2) });
        });
        using var provider = Provider(api);
        var result = Assert.Single(await provider.SearchAsync("reticle hud"));
        Assert.Equal("42", result.ModId); Assert.Equal("2.0", result.Version); Assert.NotNull(result.Thumbnail);
        Assert.Equal(new[] { 1, 2 }, pages);
        Assert.Empty(await provider.SearchAsync("reticle hud", offset: 1));
        Assert.Empty(await provider.SearchAsync("not found"));
    }

    [Fact]
    public async Task ResolveUsesNewestVersionAcrossPagesAndOnlyHostedZips()
    {
        using var api = Http(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("XF-Api-Key")));
            if (request.RequestUri!.AbsolutePath != "/api/mods/42/versions/") return Api(request);
            var page = request.RequestUri.Query.Contains("page=1") ? 1 : 2;
            return Json(new { versions = new[] { page == 1 ? Version(10, 42, File()) : Version(20, 42, File(), File(13, "alternate.ZIP"), File(14, "mod.7z")) }, pagination = Pagination(page, 2, 2) });
        });
        using var provider = Provider(api);
        var mod = await provider.ResolveAsync("https://www.ayakamods.com/mods/reticle.42/download");
        Assert.True(mod.DownloadsDirectly); Assert.Equal("Download", mod.DownloadAction); Assert.Equal("v20", mod.Version);
        Assert.Equal(2, mod.Files.Count); Assert.Equal("20:12", mod.Files[0].FileId);
        Assert.Equal("https://ayakamods.com/api/mod-versions/20/download?file=12", mod.Files[0].DownloadPage.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://ayakamods.com/mods/mod.42/")]
    [InlineData("https://ayakamods.com.evil.test/mods/mod.42/")]
    [InlineData("https://user@ayakamods.com/mods/mod.42/")]
    [InlineData("https://ayakamods.com:8080/mods/mod.42/")]
    [InlineData("https://ayakamods.com/mods/")]
    [InlineData("https://ayakamods.com/mods/mod.0/")]
    [InlineData("https://ayakamods.com/mods/mod.42/other")]
    public async Task RejectsInvalidLinksBeforeSendingRequests(string link)
    {
        using var api = Http(_ => throw new Exception("Should not send")); using var provider = Provider(api);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(link));
    }

    [Theory]
    [InlineData(7, "download_local")]
    [InlineData(3, "download_external")]
    public async Task RejectsOtherGamesAndExternalOnlyDownloads(long game, string type)
    {
        using var api = Http(request => request.RequestUri!.AbsolutePath == "/api/mods/42/"
            ? Json(new { mod = Mod(game: game, type: type) }) : Api(request, Version(20, 42, File())));
        using var provider = Provider(api);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResolveAsync("https://ayakamods.com/mods/mod.42/"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "integration key")]
    [InlineData(HttpStatusCode.Forbidden, "scope")]
    [InlineData(HttpStatusCode.NotFound, "not found")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limit")]
    public async Task ReportsApiErrorsAndRedactsTheKey(HttpStatusCode status, string expected)
    {
        using var api = Http(_ => new(status) { Content = new StringContent("""{"errors":[{"code":"no_permission","message":"Denied test-key"}]}""", Encoding.UTF8, "application/json") });
        using var provider = Provider(api);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.SearchAsync("reticle"));
        Assert.Equal(status, error.StatusCode); Assert.Contains(expected, error.Message); Assert.Contains("no_permission", error.Message);
        Assert.DoesNotContain("test-key", error.Message);
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(1, 10001, 1)]
    [InlineData(1, 1, 0)]
    public async Task InvalidPaginationFailsInsteadOfLooping(int page, int last, int total)
    {
        using var api = Http(request => request.RequestUri!.AbsolutePath == "/api/mod-games/" ? Json(Games()) :
            Json(new { mods = new[] { Mod() }, pagination = Pagination(page, last, total) }));
        using var provider = Provider(api);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.SearchAsync("reticle"));
    }

    [Theory]
    [InlineData("mod.zip", "mod.zip", false, UpdateStatus.Available)]
    [InlineData("old.zip", "new.zip", false, UpdateStatus.Available)]
    [InlineData("old.zip", "new.zip", true, UpdateStatus.Unknown)]
    public async Task UpdatesMatchVariantsAndAvoidAmbiguousReplacements(string oldName, string newName, bool multiple, UpdateStatus expected)
    {
        var latest = multiple ? Version(20, 42, File(12, newName), File(13, "alternate.zip")) : Version(20, 42, File(12, newName));
        using var api = Http(request => Api(request, latest, Version(10, 42, File(11, oldName))));
        using var provider = Provider(api);
        var update = await provider.CheckUpdateAsync(new("ayakamods", "42", "10:11", "v10"));
        Assert.Equal(expected, update.Status);
        if (expected == UpdateStatus.Available) Assert.Equal("20:12", update.File!.FileId);
        else Assert.NotNull(update.Reason);
        Assert.Equal(UpdateStatus.Current, (await provider.CheckUpdateAsync(new("ayakamods", "42", "20:12", "v20"))).Status);
    }

    [Fact]
    public async Task MissingPreviousVersionReportsUnknownAndPinnedNewerVersionIsNotDowngraded()
    {
        using var api = Http(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/mod-versions/10/" => new(HttpStatusCode.NotFound),
            "/api/mod-versions/30/" => Json(new { version = Version(30, 42, File(30)) }),
            _ => Api(request, Version(20, 42, File()))
        });
        using var provider = Provider(api);
        Assert.Equal(UpdateStatus.Unknown, (await provider.CheckUpdateAsync(new("ayakamods", "42", "10:11", "v10"))).Status);
        Assert.Equal(UpdateStatus.Current, (await provider.CheckUpdateAsync(new("ayakamods", "42", "30:30", "v30"))).Status);
    }

    [Fact]
    public async Task RedirectedDownloadNeverSendsKeyToStorageHost()
    {
        using var f = new Fixture();
        using var api = Http(request => Api(request, Version(20, 42, File())));
        var requests = 0;
        using var downloads = Http(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "ayakamods.com")
            {
                Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("XF-Api-Key")));
                Assert.Equal("/api/mod-versions/20/download", request.RequestUri.AbsolutePath);
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://storage.example/mod.zip"); return redirect;
            }
            Assert.False(request.Headers.Contains("XF-Api-Key"));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        });
        using var provider = Provider(api, downloads);
        var file = Assert.Single((await provider.ResolveAsync("https://ayakamods.com/mods/mod.42/")).Files);
        var path = Path.Combine(f.App, "temp", "mod.zip");
        await provider.DownloadAsync(file.DownloadPage.AbsoluteUri, file, path);
        Assert.Equal(2, requests); Assert.Equal(new byte[] { 1, 2, 3 }, await System.IO.File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BadDownloadsLeaveNoPartialFiles(bool unsafeRedirect)
    {
        using var f = new Fixture(); using var api = Http(request => Api(request, Version(20, 42, File())));
        using var downloads = Http(_ =>
        {
            if (!unsafeRedirect) return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2]) };
            var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri("http://storage.example/mod.zip"); return response;
        });
        using var provider = Provider(api, downloads); var file = Assert.Single((await provider.ResolveAsync("https://ayakamods.com/mods/mod.42/")).Files);
        var path = Path.Combine(f.App, "temp", "mod.zip");
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.DownloadAsync(file.DownloadPage.AbsoluteUri, file, path));
        Assert.False(System.IO.File.Exists(path)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task ForgedFileOrVersionFromAnotherModCannotDownload()
    {
        using var f = new Fixture(); using var api = Http(request => request.RequestUri!.AbsolutePath == "/api/mod-versions/20/"
            ? Json(new { version = Version(20, 99, File()) }) : Api(request, Version(20, 42, File())));
        using var downloads = Http(_ => throw new Exception("Should not download")); using var provider = Provider(api, downloads);
        var file = Assert.Single((await provider.ResolveAsync("https://ayakamods.com/mods/mod.42/")).Files);
        var path = Path.Combine(f.App, "mod.zip");
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DownloadAsync("https://evil.test/mod.zip", file, path));
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.DownloadAsync(file.DownloadPage.AbsoluteUri, file, path));
    }

    [Fact]
    public async Task QueueImportsDirectlyAndStoresSourceWithoutBrowserOrDownloadFolder()
    {
        using var f = new Fixture(); var bytes = await System.IO.File.ReadAllBytesAsync(f.Zip(f.Source("Ayaka mod")));
        using var api = Http(request => request.RequestUri!.AbsolutePath == "/api/mod-versions/20/"
            ? Json(new { version = Version(20, 42, File(size: bytes.Length)) }) : Api(request, Version(20, 42, File(size: bytes.Length))));
        using var downloads = Http(request =>
        {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("XF-Api-Key")));
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        });
        using var provider = Provider(api, downloads);
        await using var manager = new ProviderManager(f.Library, new(f.App), [provider], Path.Combine(f.App, "temp", "downloads"), _ => throw new Exception("Browser should not open"));
        await manager.InitializeAsync();
        var profile = ProfileEditor.Create("Profile"); await f.Library.SaveProfileAsync(profile);
        Assert.Equal("ayakamods", manager.FindProvider("https://ayakamods.com/mods/reticle.42/").Id);
        var file = Assert.Single((await manager.ResolveAsync("https://ayakamods.com/mods/reticle.42/")).Files);
        var job = await manager.QueueAsync(file, profile.Id); await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var state = await f.Library.LoadAsync(); var mod = Assert.Single(state.Mods);
        Assert.Equal(new SourceReference("ayakamods", "42", "20:12", "v20"), Assert.Single(mod.Sources));
        Assert.Equal(mod.Id, Assert.Single(Assert.Single(state.Profiles).Entries).ModId);
        Assert.Empty(Directory.GetFiles(Path.Combine(f.App, "temp", "downloads")));
    }
}
