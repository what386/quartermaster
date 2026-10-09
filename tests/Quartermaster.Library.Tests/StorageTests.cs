using Quartermaster.Core.Deployment;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Storage;
using Xunit;

namespace Quartermaster.Library.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task ReadOnlyOverviewDoesNotContendWithTheMutationLease()
    {
        using var f = new Fixture();
        await f.Library.ImportAsync(f.Source("Current"));
        var state = await f.Library.LoadAsync();
        await using var lease = await f.Store.AcquireLockAsync();
        var report = await new StorageManager(f.App).ReadOverviewAsync(state);
        Assert.Equal(199, report.LibraryBytes);
        Assert.Single(report.Mods);
        Assert.Empty(report.Candidates);
    }
    [Fact]
    public async Task MeasuresActualFilesAndOnlyRemovesReviewedUnusedPreviousVersions()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        var unused = await f.Library.ImportAsync(f.Source("unused", 3));
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        await f.Library.SaveProfileAsync(ProfileEditor.Add(ProfileEditor.Create("Working"), current));
        var storage = new StorageManager(f.App);
        var report = await storage.ScanAsync();
        Assert.Equal(3 * 199, report.LibraryBytes);
        Assert.Equal(1, report.Mods.Single(mod => mod.ModId == current.Id).ProfileCount);
        var candidate = Assert.Single(report.Candidates);
        Assert.Equal(old.Id, candidate.ModId);
        var result = await storage.CleanupAsync([candidate]);
        Assert.Equal(199, result.RemovedBytes); Assert.Equal(1, result.RemovedItems);
        Assert.False(Directory.Exists(f.Contents.GetModDirectory(old.Id)));
        Assert.Equal(new[] { current.Id, unused.Id }, (await f.Library.LoadAsync()).Mods.Select(mod => mod.Id));
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(unused.Id)));
    }

    [Fact]
    public async Task NewProfileReferenceProtectsAPreviouslyReviewedCandidateEvenWhenDisabled()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        var storage = new StorageManager(f.App);
        var candidate = Assert.Single((await storage.ScanAsync()).Candidates);
        await f.Library.SaveProfileAsync(ProfileEditor.SetEnabled(ProfileEditor.Add(ProfileEditor.Create("Keep"), old), old.Id, false));
        var result = await storage.CleanupAsync([candidate]);
        Assert.Equal(0, result.RemovedItems); Assert.Equal(1, result.SkippedItems);
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(old.Id)));
        Assert.Empty((await storage.ScanAsync()).Candidates);
    }

    [Fact]
    public async Task DeployedOldVersionsAreProtectedUntilPurged()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Working"), old);
        await f.Library.SaveProfileAsync(profile);
        var deployment = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents)));
        await deployment.DeployAsync(await f.Library.LoadAsync(), profile.Id, f.Game);
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        var storage = new StorageManager(f.App);
        var report = await storage.ScanAsync();
        Assert.Empty(report.Candidates);
        Assert.True(report.Mods.Single(mod => mod.ModId == old.Id).IsDeployed);
        Assert.Equal(199, report.DeployedBytes);
        await deployment.PurgeAsync(f.Game);
        report = await storage.ScanAsync();
        Assert.Equal(old.Id, Assert.Single(report.Candidates).ModId);
        Assert.Equal(0, report.DeployedBytes);
    }

    [Fact]
    public async Task OrphansAndAbandonedStagingAreCandidatesButDownloadsAndUnknownDirectoriesAreKept()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.App);
        var orphan = Path.Combine(f.App, "library", Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(f.App, "temp", "deployment-" + Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(f.App, "temp", "downloads");
        var unknown = Path.Combine(f.App, "library", "my-backup");
        foreach (var path in new[] { orphan, staging, downloads, unknown })
        {
            Directory.CreateDirectory(path); await File.WriteAllBytesAsync(Path.Combine(path, "content"), [1, 2, 3]);
            File.SetLastWriteTimeUtc(Path.Combine(path, "content"), DateTime.UtcNow.AddDays(-2));
            Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        }
        var storage = new StorageManager(f.App);
        var report = await storage.ScanAsync();
        Assert.Equal(2, report.Candidates.Count);
        Assert.Equal(6, report.LibraryBytes); Assert.Equal(6, report.TemporaryBytes);
        var result = await storage.CleanupAsync(report.Candidates);
        Assert.Equal(6, result.RemovedBytes);
        Assert.True(Directory.Exists(downloads)); Assert.True(Directory.Exists(unknown));
        Assert.False(Directory.Exists(orphan)); Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task ChangedContentRequiresAnotherReviewAndForgedPathsCannotBeDeleted()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        var storage = new StorageManager(f.App);
        var candidate = Assert.Single((await storage.ScanAsync()).Candidates);
        await File.WriteAllBytesAsync(Path.Combine(f.Contents.GetModDirectory(old.Id), "new-file"), [1]);
        var result = await storage.CleanupAsync([candidate, candidate with { RelativePath = "../game" }]);
        Assert.Equal(2, result.SkippedItems); Assert.Equal(0, result.RemovedItems);
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(old.Id)));
        Assert.True(Directory.Exists(f.Game));
    }

    [Fact]
    public async Task LinksAndUnreadableDeploymentLedgersPreventCleanup()
    {
        using var f = new Fixture();
        var mod = await f.Library.ImportAsync(f.Source("mod"));
        var storage = new StorageManager(f.App);
        await File.WriteAllTextAsync(Path.Combine(f.App, "deployment.lock"), "invalid json");
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ScanAsync());
        File.Delete(Path.Combine(f.App, "deployment.lock"));
        if (OperatingSystem.IsWindows()) return; // Symlink creation requires developer mode or elevated privileges.
        var link = Path.Combine(f.Contents.GetModDirectory(mod.Id), "linked");
        Directory.CreateSymbolicLink(link, f.Game);
        try { await Assert.ThrowsAsync<InvalidDataException>(() => storage.ScanAsync()); }
        finally { Directory.Delete(link); }
        Assert.True(File.Exists(Path.Combine(f.Game, Fixture.Archive)));
    }

    [Fact]
    public async Task BusyLibraryAndCancellationLeaveContentUntouched()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        var storage = new StorageManager(f.App);
        var candidate = Assert.Single((await storage.ScanAsync()).Candidates);
        await using (var lease = await f.Store.AcquireLockAsync())
            await Assert.ThrowsAsync<IOException>(() => storage.CleanupAsync([candidate]));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.CleanupAsync([candidate], cancelled.Token));
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(old.Id)));
    }

    [Fact]
    public async Task IncompleteDeploymentProtectsAllLibraryContentEvenWithAnEmptyLedger()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var current = await f.Library.ImportAsync(f.Source("current", 2));
        await f.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        var storage = new StorageManager(f.App);
        var reviewed = (await storage.ScanAsync()).Candidates;
        var ledger = DeploymentLedger.Empty(f.Game) with { Status = DeploymentStatus.Deploying };
        await File.WriteAllTextAsync(Path.Combine(f.App, "deployment.lock"), global::System.Text.Json.JsonSerializer.Serialize(ledger));
        var report = await storage.ScanAsync();
        Assert.True(report.IsDeploymentUncertain); Assert.Empty(report.Candidates);
        Assert.Equal(1, (await storage.CleanupAsync(reviewed)).SkippedItems);
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(old.Id)));
    }
}
