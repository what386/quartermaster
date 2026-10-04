using System.Security.Cryptography;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Tests;
using Quartermaster.Providers.Downloads;
using Quartermaster.Providers.Providers;
using Quartermaster.Providers.Protocol;
using Xunit;

namespace Quartermaster.Providers.Tests;

public sealed class DownloadTests
{
    private sealed class Scanner() : DownloadScanner(TimeSpan.FromMilliseconds(10));
    private sealed class FakeProvider(ProviderUpdate? update = null) : IModProvider
    {
        public string Id => "test";
        public bool CanHandle(Uri link) => link.IsAbsoluteUri && link.Host == "example.com";
        public DownloadScanner CreateScanner() => new Scanner();
        public Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default) => Task.FromResult(new ProviderMod("1", "Example", "", "1", new(link), [Expected([1])]));
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ProviderUpdate> CheckUpdateAsync(Quartermaster.Library.Mods.SourceReference source, CancellationToken ct = default) => Task.FromResult(update ?? new ProviderUpdate(UpdateStatus.Current));
    }
    private static ProviderFile Expected(byte[] bytes) => new("test", "1", "2", "Mod", "mod.zip", "v1",
        new("https://example.com/mod/files/2"), Sha256: Convert.ToHexString(SHA256.HashData(bytes)));

    [Fact]
    public async Task ScannerRejectsWrongHashAndPartialFilesThenCopiesRenamedCompletedFile()
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        byte[] bytes = [1, 2, 3]; var expected = Expected(bytes);
        await File.WriteAllBytesAsync(Path.Combine(folder, "wrong.zip"), [3, 2, 1]);
        var partial = Path.Combine(folder, "mod.zip.crdownload"); await File.WriteAllBytesAsync(partial, bytes);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var destination = Path.Combine(f.App, "cache.zip");
        var task = new Scanner().WaitForDownloadAsync(expected, () => [folder], destination, cancellation.Token);
        await Task.Delay(80); Assert.False(task.IsCompleted);
        var complete = Path.Combine(folder, "renamed (1).zip"); File.Move(partial, complete);
        await task; Assert.Equal(bytes, await File.ReadAllBytesAsync(destination)); Assert.True(File.Exists(complete));
        Assert.Empty(Directory.GetFiles(f.App, "*.tmp"));
    }
    [Theory]
    [InlineData("http://example.com/mod")]
    [InlineData("nxm://helldivers2/mods/1/files/2")]
    [InlineData("https://user@example.com/mod")]
    [InlineData("relative")]
    public void BrowserMethodRejectsNonPublicDownloadPages(string link)
    {
        var file = Expected([1]) with { DownloadPage = new(link, UriKind.RelativeOrAbsolute) };
        var opened = false;
        Assert.Throws<ArgumentException>(() => new Scanner().OpenDownloadPage(file, _ => opened = true));
        Assert.False(opened);
    }
    [Fact]
    public async Task CacheFilesystemErrorsSurfaceInsteadOfWaitingForever()
    {
        using var f = new Fixture(); await File.WriteAllBytesAsync(Path.Combine(f.Root, "mod.zip"), [1]);
        var blocked = Path.Combine(f.Root, "blocked"); await File.WriteAllTextAsync(blocked, "file, not a directory");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => new Scanner().WaitForDownloadAsync(Expected([1]), () => [f.Root], Path.Combine(blocked, "mod.zip"), ct.Token));
    }

    [Fact]
    public async Task ScannerCancellationDoesNotCreateDestination()
    {
        using var f = new Fixture(); using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        var destination = Path.Combine(f.App, "cache.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new Scanner().WaitForDownloadAsync(Expected([1]), () => [f.Root], destination, ct.Token));
        Assert.False(File.Exists(destination));
    }
    [Fact]
    public async Task QueueDeduplicatesImportsIntoProfileAndOpensExactFilePage()
    {
        using var f = new Fixture(); var profile = ProfileEditor.Create("Profile"); await f.Library.SaveProfileAsync(profile);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var archive = f.Zip(f.Source("mod")); var expected = Expected(await File.ReadAllBytesAsync(archive));
        Uri? opened = null;
        await using var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"), uri => opened = uri);
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var job = await manager.QueueAsync(expected, profile.Id);
        var duplicate = await manager.QueueAsync(expected, profile.Id); Assert.Equal(job.Id, duplicate.Id);
        manager.OpenDownloadPage(job.Id); Assert.Equal(expected.DownloadPage, opened);
        File.Copy(archive, Path.Combine(folder, "mod (1).zip"));
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var state = await f.Library.LoadAsync(); var mod = Assert.Single(state.Mods);
        Assert.Equal("2", Assert.Single(mod.Sources).FileId); Assert.Equal(mod.Id, Assert.Single(Assert.Single(state.Profiles).Entries).ModId);
        Assert.True(File.Exists(Path.Combine(folder, "mod (1).zip"))); Assert.Empty(Directory.GetFiles(Path.Combine(f.App, "cache")));
    }
    [Fact]
    public async Task CancelledQueuePersistsAndRetryResumesAfterRestart()
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var zip = f.Zip(f.Source("mod")); var expected = Expected(await File.ReadAllBytesAsync(zip)); Guid id;
        await using (var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache")))
        {
            await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]); id = (await manager.QueueAsync(expected)).Id;
            await manager.CancelAsync(id); Assert.Equal(DownloadStatus.Cancelled, Assert.Single(manager.State.Jobs).Status);
        }
        await using var resumed = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"));
        await resumed.InitializeAsync(); Assert.Equal(DownloadStatus.Cancelled, Assert.Single(resumed.State.Jobs).Status);
        await resumed.RetryAsync(id); File.Copy(zip, Path.Combine(folder, "mod.zip"));
        await resumed.WaitForJobAsync(id).WaitAsync(TimeSpan.FromSeconds(5)); Assert.Single((await f.Library.LoadAsync()).Mods);
    }
    [Fact]
    public async Task NormalExitClearsPendingBrowserRequestsWithoutDeletingBrowserFiles()
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var zip = f.Zip(f.Source("mod")); var expected = Expected(await File.ReadAllBytesAsync(zip));
        await using (var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache")))
        { await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]); await manager.QueueAsync(expected); }
        File.Copy(zip, Path.Combine(folder, "mod.zip"));
        await using var resumed = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"));
        await resumed.InitializeAsync(); Assert.Empty(resumed.State.Jobs); Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.True(File.Exists(Path.Combine(folder, "mod.zip")));
    }
    [Fact]
    public async Task AttachZipBypassesRecognitionAndPreservesProfileUpgradeState()
    {
        using var f = new Fixture(); var old = await f.Library.ImportAsync(f.Source("old", 1));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Profile"), old);
        profile = ProfileEditor.AddGroup(profile, "Equipment", [old.Id]);
        profile = ProfileEditor.SetEnabled(profile, old.Id, false); await f.Library.SaveProfileAsync(profile);
        var archive = f.Zip(f.Source("new", 2), "already-downloaded.zip");
        var bytes = await File.ReadAllBytesAsync(archive);
        await using var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([Path.Combine(f.Root, "empty")]);
        var job = await manager.QueueAsync(Expected([9, 9]), replacesModId: old.Id);
        await manager.AttachZipAsync(job.Id, archive);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var state = await f.Library.LoadAsync(); var entry = Assert.Single(Assert.Single(state.Profiles).Entries);
        Assert.NotEqual(old.Id, entry.ModId); Assert.False(entry.Enabled); Assert.Equal(profile.Groups[0].Id, entry.GroupId);
        Assert.Equal("2", Assert.Single(state.Mods.Single(mod => mod.Id == entry.ModId).Sources).FileId);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(archive));
        await manager.RemoveAsync(job.Id); Assert.Empty(manager.State.Jobs);
        Assert.Equal(2, (await f.Library.LoadAsync()).Mods.Count);
    }
    [Fact]
    public async Task InvalidManualZipFailsWithoutImportAndCanBeReplaced()
    {
        using var f = new Fixture(); var broken = Path.Combine(f.Root, "broken.zip"); await File.WriteAllBytesAsync(broken, [1, 2, 3]);
        await using var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([Path.Combine(f.Root, "empty")]);
        var job = await manager.QueueAsync(Expected([9]));
        await Assert.ThrowsAsync<InvalidDataException>(() => manager.AttachZipAsync(job.Id, broken));
        Assert.Equal(DownloadStatus.Failed, Assert.Single(manager.State.Jobs).Status); Assert.Empty((await f.Library.LoadAsync()).Mods);
        var valid = f.Zip(f.Source("mod")); await manager.AttachZipAsync(job.Id, valid);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status); Assert.Single((await f.Library.LoadAsync()).Mods);
    }
    [Fact]
    public async Task RemovingWaitingRequestStopsScannerAndRemovesPersistedEntry()
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var archive = f.Zip(f.Source("mod")); var expected = Expected(await File.ReadAllBytesAsync(archive));
        var store = new DownloadStore(f.App);
        await using var manager = new ProviderManager(f.Library, store, [new FakeProvider()], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]); var job = await manager.QueueAsync(expected);
        await manager.RemoveAsync(job.Id); File.Copy(archive, Path.Combine(folder, "mod.zip"));
        Assert.Empty(manager.State.Jobs); Assert.Empty((await store.LoadAsync()).Jobs); Assert.Empty((await f.Library.LoadAsync()).Mods);
    }
    [Fact]
    public async Task QueuePersistedByCrashStillResumes()
    {
        using var f = new Fixture(); var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var archive = f.Zip(f.Source("mod")); var expected = Expected(await File.ReadAllBytesAsync(archive));
        var store = new DownloadStore(f.App); var job = new DownloadJob(Guid.NewGuid(), expected);
        await store.SaveAsync(new([folder], [job])); File.Copy(archive, Path.Combine(folder, "mod.zip"));
        await using var manager = new ProviderManager(f.Library, store, [new FakeProvider()], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status); Assert.Single((await f.Library.LoadAsync()).Mods);
    }
    [Fact]
    public async Task InvalidArchiveFailsWithoutImportAndCanRetry()
    {
        using var f = new Fixture(); byte[] bytes = [1, 2, 3]; var expected = Expected(bytes);
        await using var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider()], Path.Combine(f.App, "cache"));
        var failures = new List<DownloadJob>(); manager.DownloadFailed += failures.Add;
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([f.Root]); var job = await manager.QueueAsync(expected);
        await File.WriteAllBytesAsync(Path.Combine(f.Root, "mod.zip"), bytes);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DownloadStatus.Failed, Assert.Single(manager.State.Jobs).Status); Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Equal(job.Id, Assert.Single(failures).Id); Assert.False(string.IsNullOrWhiteSpace(failures[0].Error));
    }
    [Fact]
    public async Task LinkDispatchUsesProviderCapabilitiesAndRejectsUnsupportedHosts()
    {
        using var f = new Fixture(); var provider = new FakeProvider();
        await using var manager = new ProviderManager(f.Library, new(f.App), [provider], Path.Combine(f.App, "cache"));
        Assert.Same(provider, manager.FindProvider("https://example.com/mod/1"));
        Assert.Equal("1", (await manager.ResolveAsync("https://example.com/mod/1")).ModId);
        Assert.Throws<NotSupportedException>(() => manager.FindProvider("https://unregistered.example/mod/1"));
        Assert.Throws<ArgumentException>(() => manager.FindProvider("https://user@example.com/mod/1"));
        Assert.Throws<ArgumentException>(() => manager.FindProvider("not a link"));
    }
    [Fact]
    public async Task CheckingUpdatesOnlyRecordsAvailabilityAndUpgradeIsExplicit()
    {
        using var f = new Fixture(); var old = await f.Library.ImportAsync(f.Source("old", 1)); var other = await f.Library.ImportAsync(f.Source("other", 2));
        await f.Library.SetSourcesAsync(old.Id, [new("test", "1", "1", "old")]);
        await f.Library.SetSourcesAsync(other.Id, [new("test", "1", "1", "old")]);
        var profile = ProfileEditor.Add(ProfileEditor.Create("Profile"), old); await f.Library.SaveProfileAsync(profile);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var expected = Expected([1]);
        await using var manager = new ProviderManager(f.Library, new(f.App), [new FakeProvider(new(UpdateStatus.Available, expected))], Path.Combine(f.App, "cache"));
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        await manager.CheckUpdatesAsync([old.Id]);
        var state = await f.Library.LoadAsync(); Assert.Equal("2", Assert.Single(state.UpdateChecks).AvailableFileId);
        Assert.Equal(old.Id, Assert.Single(state.UpdateChecks).ModId); Assert.Empty(manager.State.Jobs);
        Assert.Equal(old.Id, Assert.Single(Assert.Single(state.Profiles).Entries).ModId);
        var job = await manager.QueueUpdateAsync(old.Id); Assert.Equal(old.Id, job.ReplacesModId);
        Assert.Equal(DownloadStatus.Waiting, job.Status); Assert.Single(manager.State.Jobs);
        await manager.CancelAsync(job.Id);
    }

    [Fact]
    public async Task CredentialsArePrivateAndSeparateFromQueueMetadata()
    {
        using var f = new Fixture(); var keys = new ApiKeyStore(f.App);
        await keys.SetAsync("nexusmods", "test-secret"); Assert.Equal("test-secret", await keys.GetAsync("nexusmods"));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.App, "credentials", "nexusmods.key")));
        await Assert.ThrowsAsync<ArgumentException>(() => keys.SetAsync("../escape", "test-secret"));
        await keys.SetAsync("nexusmods", null); Assert.Null(await keys.GetAsync("nexusmods"));
    }
    [Fact]
    public void InboxStoresAndConsumesSignedLinksPrivately()
    {
        using var f = new Fixture(); var inbox = new NxmInbox(f.App); var link = "nxm://helldivers2/mods/1/files/2?key=grant&expires=9999999999&user_id=3";
        inbox.Enqueue(link); var path = Assert.Single(inbox.Pending); Assert.Equal(link, inbox.Read(path));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        inbox.Remove(path); Assert.Empty(inbox.Pending);
        Assert.Throws<ArgumentException>(() => inbox.Enqueue("https://example.com"));
    }
    [Fact]
    public void DesktopEntryQuotesPathsAndEscapesDesktopPlaceholders()
    {
        var entry = NxmProtocol.CreateDesktopEntry(["/opt/My App/Quartermaster%test"]);
        Assert.Contains("Exec=\"/opt/My App/Quartermaster%%test\" %u", entry);
        Assert.Throws<ArgumentException>(() => NxmProtocol.CreateDesktopEntry(["/a\nInjected=true"]));
    }
}
