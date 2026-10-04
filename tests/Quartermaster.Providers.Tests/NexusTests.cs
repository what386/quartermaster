using System.Net;
using System.Text;
using System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;
using Quartermaster.Library.Tests;
using Quartermaster.Providers.Providers;
using Quartermaster.Providers.Providers.NexusMods;
using Xunit;

namespace Quartermaster.Providers.Tests;

public sealed class NexusTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(request => Task.FromResult(send(request))));
    private static NexusClient Client(HttpClient api, HttpClient? download = null) => new(_ => Task.FromResult<string?>("test-api-key"), api, download);

    [Theory]
    [InlineData("https://www.nexusmods.com/helldivers2/mods/123", 123, null)]
    [InlineData("https://nexusmods.com/games/helldivers2/mods/123?tab=files&file_id=456", 123L, 456L)]
    [InlineData("nxm://helldivers2/mods/123/files/456?key=secret&expires=9999999999&user_id=7", 123L, 456L)]
    public void ParsesHd2LinksWithoutExposingSignedGrant(string url, long mod, long? file)
    { var parsed = NexusLink.Parse(url); Assert.Equal(mod, parsed.ModId); Assert.Equal(file, parsed.FileId); Assert.DoesNotContain("secret", parsed.ToString()); }
    [Theory]
    [InlineData("https://evil.com/helldivers2/mods/123")]
    [InlineData("https://www.nexusmods.com/skyrim/mods/123")]
    [InlineData("http://nexusmods.com/helldivers2/mods/123")]
    [InlineData("nxm://skyrim/mods/1/files/2")]
    [InlineData("nxm://helldivers2/mods/1/files/2?key=abc")]
    [InlineData("https://nexusmods.com/helldivers2/mods/1?file_id=2&file_id=3")]
    [InlineData("https://user@nexusmods.com/helldivers2/mods/1")]
    public void RejectsUnrelatedOrMalformedLinks(string url) => Assert.Throws<ArgumentException>(() => NexusLink.Parse(url));
    [Fact]
    public async Task ApiUsesPersonalKeyAndFixedHd2Routes()
    {
        using var api = Http(request =>
        {
            Assert.Equal("api.nexusmods.com", request.RequestUri!.Host);
            Assert.Equal("/v1/games/helldivers2/mods/123.json", request.RequestUri.AbsolutePath);
            Assert.Equal("test-api-key", Assert.Single(request.Headers.GetValues("apikey")));
            Assert.Equal("Quartermaster", Assert.Single(request.Headers.GetValues("Application-Name")));
            return Json(new { mod_id = 123, name = "Test", summary = "Description", version = "1", available = true });
        });
        using var client = Client(api); Assert.Equal(123, (await client.GetModAsync(123)).Id);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ErrorsDoNotIncludeResponseOrCredentials(HttpStatusCode status)
    {
        using var api = Http(_ => new(status) { Content = new StringContent("secret-response test-api-key") });
        using var client = Client(api); var ex = await Assert.ThrowsAsync<NexusApiException>(() => client.ValidateAsync());
        Assert.Equal(status, ex.Status); Assert.DoesNotContain("secret-response", ex.Message); Assert.DoesNotContain("test-api-key", ex.Message);
    }
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task HashNotFoundIsNotARequestFailure(HttpStatusCode status)
    {
        using var api = Http(_ => new(status)); using var client = Client(api);
        Assert.False(await client.MatchesHashAsync(1, 2, new string('a', 32)));
    }
    [Fact]
    public async Task UpdateFollowsFileChainInsteadOfNewestOptionalUpload()
    {
        using var api = Http(_ => Json(new { files = new[] { File(2), File(3), File(99) }, file_updates = new[] { new { old_file_id = 1, new_file_id = 2 }, new { old_file_id = 2, new_file_id = 3 } } }));
        using var client = Client(api); var result = await new NexusAdapter(client).CheckUpdateAsync(new("nexusmods", "123", "1"));
        Assert.Equal(UpdateStatus.Available, result.Status); Assert.Equal("3", result.File!.FileId);
    }
    [Fact]
    public async Task BranchingUpdateChainRequiresChoice()
    {
        using var api = Http(_ => Json(new { files = new[] { File(2), File(3) }, file_updates = new[] { new { old_file_id = 1, new_file_id = 2 }, new { old_file_id = 1, new_file_id = 3 } } }));
        using var client = Client(api); Assert.Equal(UpdateStatus.Unknown, (await new NexusAdapter(client).CheckUpdateAsync(new("nexusmods", "123", "1"))).Status);
    }
    [Fact]
    public async Task SearchUsesTokenMatchingSoBingusFindsBingusSharedLoader()
    {
        using var api = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("/v2/graphql", request.RequestUri!.AbsolutePath);
            using var data = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Contains("thumbnailUrl pictureUrl", data.RootElement.GetProperty("query").GetString());
            var filter = data.RootElement.GetProperty("variables").GetProperty("filter");
            Assert.Equal("helldivers2", filter.GetProperty("gameDomainName")[0].GetProperty("value").GetString());
            Assert.False(filter.TryGetProperty("name", out _));
            var name = filter.GetProperty("nameStemmed")[0];
            Assert.Equal("bingus", name.GetProperty("value").GetString());
            Assert.Equal("MATCHES", name.GetProperty("op").GetString());
            return Json(new { data = new { mods = new { nodes = new[] { new { modId = 16292, name = "Bingus Shared Loader", summary = "Standalone mod loader", version = "18", thumbnailUrl = "https://images.nexusmods.com/thumb.jpg", pictureUrl = "https://images.nexusmods.com/full.jpg" } } } } });
        }));
        using var client = Client(api);
        var mod = Assert.Single(await client.SearchAsync(" bingus "));
        Assert.Equal("16292", mod.ModId); Assert.Equal("Bingus Shared Loader", mod.Name);
        Assert.Equal("https://www.nexusmods.com/helldivers2/mods/16292", mod.Page.AbsoluteUri);
        Assert.Equal("https://images.nexusmods.com/thumb.jpg", mod.Thumbnail!.AbsoluteUri);
    }
    [Theory]
    [InlineData(null, "https://images.nexusmods.com/full.jpg", "https://images.nexusmods.com/full.jpg")]
    [InlineData("invalid", null, null)]
    [InlineData("file:///tmp/image.png", "https://images.nexusmods.com/full.jpg", "https://images.nexusmods.com/full.jpg")]
    public async Task SearchHandlesMissingOrInvalidThumbnails(string? thumbnail, string? picture, string? expected)
    {
        using var api = Http(_ => Json(new { data = new { mods = new { nodes = new[] {
            new { modId = 123, name = "Example", summary = "Description", version = "1", thumbnailUrl = thumbnail, pictureUrl = picture }
        } } } }));
        using var client = Client(api);
        Assert.Equal(expected, Assert.Single(await client.SearchAsync("example")).Thumbnail?.AbsoluteUri);
    }
    [Fact]
    public async Task FreeSignedLinkDownloadsWithoutSendingApiKeyToCdn()
    {
        using var f = new Fixture(); var destination = Path.Combine(f.App, "mod.zip");
        using var api = Http(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("validate.json")) return Json(new { user_id = 7, name = "User", is_premium = false });
            Assert.Equal("?key=signed-grant&expires=9999999999", request.RequestUri.Query);
            return Json(new[] { new { URI = "https://cdn.example.com/mod.zip?token=cdn-grant" } });
        });
        using var downloads = Http(request =>
        { Assert.False(request.Headers.Contains("apikey")); Assert.Equal("cdn.example.com", request.RequestUri!.Host); return new(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) }; });
        using var client = Client(api, downloads);
        await client.DownloadAsync(NexusLink.Parse("nxm://helldivers2/mods/123/files/456?key=signed-grant&expires=9999999999&user_id=7"), 456, destination);
        Assert.Equal(new byte[] { 1, 2, 3 }, await System.IO.File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(f.App, "*.tmp"));
    }
    [Theory]
    [InlineData("nxm://helldivers2/mods/123/files/456")]
    [InlineData("nxm://helldivers2/mods/123/files/456?key=grant&expires=1")]
    [InlineData("nxm://helldivers2/mods/123/files/456?key=grant&expires=9999999999&user_id=8")]
    public async Task FreeAccountRequiresFreshGrantForMatchingAccount(string link)
    {
        using var api = Http(request => { Assert.EndsWith("validate.json", request.RequestUri!.AbsolutePath); return Json(new { user_id = 7, name = "User", is_premium = false }); });
        using var client = Client(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(NexusLink.Parse(link), 456, "unused.zip"));
    }
    [Fact]
    public async Task FailedDownloadLeavesExistingDestinationUntouched()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "mod.zip"); await System.IO.File.WriteAllBytesAsync(path, [42]);
        using var api = Http(request => request.RequestUri!.AbsolutePath.EndsWith("validate.json")
            ? Json(new { user_id = 7, name = "User", is_premium = true }) : Json(new[] { new { URI = "https://cdn.example.com/mod.zip" } }));
        using var downloads = Http(_ => new(HttpStatusCode.OK) { Content = new StringContent("<html>error</html>", Encoding.UTF8, "text/html") });
        using var client = Client(api, downloads);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(NexusLink.Parse("https://nexusmods.com/helldivers2/mods/123"), 456, path));
        Assert.Equal(new byte[] { 42 }, await System.IO.File.ReadAllBytesAsync(path)); Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
    }
    [Fact]
    public async Task NxmCompletesExistingQueueItemWithoutPersistingSecrets()
    {
        using var f = new Fixture(); var zip = f.Zip(f.Source("mod")); var bytes = await System.IO.File.ReadAllBytesAsync(zip);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        using var api = Http(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("validate.json")) return Json(new { user_id = 7, name = "User", is_premium = false });
            if (path.EndsWith("download_link.json")) return Json(new[] { new { URI = "https://cdn.example.com/mod.zip" } });
            if (path.EndsWith("files.json")) return Json(new { files = new[] { File(456) }, file_updates = Array.Empty<object>() });
            return Json(new { mod_id = 123, name = "Test", summary = "Summary", version = "1", available = true });
        });
        using var downloads = Http(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var client = Client(api, downloads); var provider = new NexusAdapter(client);
        await using var manager = new ProviderManager(f.Library, new(f.App), [provider], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var file = Assert.Single((await provider.ResolveAsync("https://nexusmods.com/helldivers2/mods/123")).Files);
        var job = await manager.QueueAsync(file);
        await manager.HandleNxmAsync("nxm://helldivers2/mods/123/files/456?key=secret-grant&expires=9999999999&user_id=7");
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        Assert.Single((await f.Library.LoadAsync()).Mods);
        var queue = await System.IO.File.ReadAllTextAsync(Path.Combine(f.App, "downloads.json"));
        Assert.DoesNotContain("secret-grant", queue); Assert.DoesNotContain("test-api-key", queue);
    }

    private static object File(int id) => new { file_id = id, name = "Mod", file_name = "mod.zip", version = "1", category_id = 1, is_primary = true };
}
