using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Tests;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Providers.Tests;

public sealed class ManualUpdateTests
{
    [Theory]
    [InlineData("ThingDoer-v1.zip", "ThingDoer-v2.zip", true)]
    [InlineData("homing stim.zip", "homing stim.zip", true)]
    [InlineData("homing stim.zip", "homing stim (1).zip", true)]
    [InlineData("homing stim.zip", "homing stim (2).zip", true)]
    [InlineData("homing stim (2).zip", "homing stim (12).zip", true)]
    [InlineData("Weakpoint_LockOn_All_In_One_2.0.zip", "Weakpoint_LockOn_All_In_One_3.0.zip", true)]
    [InlineData("Weakpoint_LockOn_All_In_One_3.0.zip", "Weakpoint_LockOn_All_In_One_2.0.zip", true)]
    [InlineData("ThingDoer-v1.2.0.zip", "ThingDoer-v1.3.0 (1).zip", true)]
    [InlineData("Thing Doer 1.0.zip", "Thing_Doer_2.0.ZIP", true)]
    [InlineData("ThingDoer-2026.04.29.zip", "ThingDoer-2026.10.06.zip", true)]
    [InlineData("ThingDoer-v1-red.zip", "ThingDoer-v2-blue.zip", false)]
    [InlineData("HD2 Reticle-v1.zip", "HD1 Reticle-v2.zip", false)]
    [InlineData("ThingDoer-v1.zip", "OtherThing-v2.zip", false)]
    [InlineData("ThingDoer-v1.zip", "ThingDoer-v2.zip.crdownload", false)]
    [InlineData("Impatient Diver 16624 4 2026-09-29T20-58Z VTvWcJ8gG.zip", "Impatient Diver 16624 5 2026-10-06T08-12Z AbCd123.zip", true)]
    [InlineData("ReticleAmmoHUD 1.1.7 16467 1.1.7 2026-10-01T03-09Z hpXU1scj1.zip", "ReticleAmmoHUD 1.1.8 16467 1.1.8 2026-10-06T08-12Z AbCd123.zip", true)]
    [InlineData("Impatient Diver 16624 4 2026-09-29T20-58Z VTvWcJ8gG.zip", "Impatient Diver-16624-5-1791200000.zip", true)]
    [InlineData("ReticleAmmoHUD-16467-1-1-7-1791200000.zip", "ReticleAmmoHUD-16467-1-1-8-1791300000.zip", true)]
    [InlineData("ReticleAmmoHUD-16467-1-1-7-1791200000.zip", "ReticleAmmoHUD-16467-2-0-0-1791300000.zip", true)]
    [InlineData("ReticleAmmoHUD-16467-1-1-7-1791200000.zip", "ReticleAmmoHUD-99999-1-1-8-1791300000.zip", false)]
    public void FilenameMatchingKeepsVariantsAndGameNamesDistinct(string installed, string downloaded, bool matches) =>
        Assert.Equal(matches, DownloadNames.Matches(installed, downloaded));

    [Fact]
    public void TokenRankingPrefersSameModAndExtractsVersions()
    {
        var expected = Guid.NewGuid(); var variant = Guid.NewGuid();
        var results = DownloadNames.Rank("Weakpoint_LockOn_All_In_One_3.0.zip", new[]
        {
            (variant, "Weakpoint_LockOn_Standalone_2.0.zip"),
            (Guid.NewGuid(), "Different_mod_1.0.zip"),
            (expected, "Weakpoint-LockOn-All-In-One-2.0.zip")
        });
        Assert.Equal(expected, results[0].ModId); Assert.Equal(1, results[0].Score);
        Assert.Equal(variant, results[1].ModId); Assert.InRange(results[1].Score, 0.1, 0.9);
        Assert.Equal(new Version(3, 0, 0, 0), DownloadNames.Parse("Weakpoint_LockOn_All_In_One_3.0.zip").Version);
    }

    [Theory]
    [InlineData("homing stim (1).zip", "1")]
    [InlineData("homing stim (2).zip", "2")]
    public void ParenthesizedNumbersAreNameTokensNotVersions(string filename, string number)
    {
        var parsed = DownloadNames.Parse(filename);
        Assert.Null(parsed.Version);
        Assert.Contains(number, parsed.Tokens);
        Assert.Equal(new Version(1, 3, 0, 0), DownloadNames.Parse("ThingDoer-v1.3.0 (2).zip").Version);
    }

    [Fact]
    public async Task NumberedCopiesMatchBySimilarityAndRecencyWithoutReimportingOldDownloads()
    {
        using var f = new Fixture();
        var initial = f.Zip(f.Source("original", 1), "homing stim.zip");
        var mod = await f.Library.ImportAsync(initial);
        await f.Library.SetPageLinkAsync(mod.Id, "https://mods.example/homing-stim");
        await f.Library.SaveProfileAsync(ProfileEditor.Add(ProfileEditor.Create("Profile"), mod));
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        File.Copy(initial, Path.Combine(folder, "homing stim.zip"));
        File.Copy(initial, Path.Combine(folder, "homing stim (1).zip"));
        await using var manager = new ProviderManager(f.Library, new(f.App), [], Path.Combine(f.App, "temp", "downloads"), _ => { });
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var job = await manager.QueueManualUpdateAsync(mod.Id);
        await Task.Delay(1200); Assert.Equal(DownloadStatus.Waiting, Assert.Single(manager.State.Jobs).Status);
        var update = f.Zip(f.Source("update", 2), "update.zip");
        var browserCopy = Path.Combine(folder, "homing stim (2).zip"); File.Copy(update, browserCopy);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var updated = (await f.Library.LoadAsync()).Mods.Single(item => !item.Superseded);
        Assert.Equal("homing stim (2).zip", updated.ImportedFileName);
        Assert.Equal("homing stim (2)", updated.Name);
        Assert.True(File.Exists(browserCopy));
        var next = await manager.QueueManualUpdateAsync(updated.Id);
        File.Copy(f.Zip(f.Source("newest", 3), "newest.zip"), Path.Combine(folder, "homing stim (3).zip"));
        await manager.WaitForJobAsync(next.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Complete, manager.State.Jobs.Single(item => item.Id == next.Id).Status);
        Assert.Equal("homing stim (3).zip", (await f.Library.LoadAsync()).Mods.Single(item => !item.Superseded).ImportedFileName);
    }

    [Theory]
    [InlineData("ThingDoer-1023.zip", "ThingDoer-2291.zip")]
    [InlineData("ThingDoer-2291.zip", "ThingDoer-1023.zip")]
    [InlineData("ThingDoer-1023.zip", "Thing_Doer_2291.zip")]
    [InlineData("WeakpointLockOn.zip", "WeakpoinLockOn.zip")]
    public async Task SimilarUnversionedDownloadsNeedANewerTimestamp(string installed, string downloaded)
    {
        using var f = new Fixture();
        var initial = f.Zip(f.Source("original", 1), installed);
        var mod = await f.Library.ImportAsync(initial);
        await f.Library.SetPageLinkAsync(mod.Id, "https://mods.example/mod");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        var baseline = DateTime.UtcNow.AddDays(1);
        var original = Path.Combine(folder, installed); File.Copy(initial, original);
        File.SetLastWriteTimeUtc(original, baseline);
        await using var manager = new ProviderManager(f.Library, new(f.App), [], Path.Combine(f.App, "temp", "downloads"), _ => { });
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var job = await manager.QueueManualUpdateAsync(mod.Id);
        var path = Path.Combine(folder, downloaded);
        File.Copy(f.Zip(f.Source("update", 2), "update.zip"), path);
        await Task.Delay(1200);
        Assert.Equal(DownloadStatus.Waiting, Assert.Single(manager.State.Jobs).Status);
        Assert.Equal(mod.Id, Assert.Single((await f.Library.LoadAsync()).Mods).Id);
        File.SetLastWriteTimeUtc(path, baseline.AddMinutes(1));
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        Assert.Equal(downloaded, (await f.Library.LoadAsync()).Mods.Single(item => !item.Superseded).ImportedFileName);
    }

    [Theory]
    [InlineData("ThingDoer-1023.zip", "ThingDoer-2291.zip", 0.85, 0.99)]
    [InlineData("homing stim.zip", "homing stim (2).zip", 0.85, 0.99)]
    [InlineData("WeakpointLockOn.zip", "WeakpoinLockOn.zip", 0.85, 0.99)]
    [InlineData("Weakpoint_LockOn_All_In_One_red.zip", "Weakpoint_LockOn_All_In_One_blue.zip", 0, 0.84)]
    [InlineData("HD2 Reticle.zip", "HD1 Reticle.zip", 0, 0.84)]
    [InlineData("ReticleAmmoHUD-16467-1-1-7-1791200000.zip", "ReticleAmmoHUD-99999-1-1-8-1791300000.zip", 0, 0)]
    public void SimilarityWeightsReleaseNumbersAndPreservesVariants(string installed, string downloaded, double min, double max) =>
        Assert.InRange(DownloadNames.Score(installed, downloaded), min, max);

    [Fact]
    public async Task LowerVersionsWarnAndCanBeImportedWithConfirmation()
    {
        using var f = new Fixture();
        var mod = await f.Library.ImportAsync(f.Zip(f.Source("original", 1), "ThingDoer-v3.zip"));
        await f.Library.SetPageLinkAsync(mod.Id, "https://mods.example/mod");
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await using var manager = new ProviderManager(f.Library, new(f.App), [], Path.Combine(f.App, "temp", "downloads"), _ => { });
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var failures = 0; manager.DownloadFailed += _ => failures++;
        var job = await manager.QueueManualUpdateAsync(mod.Id);
        var path = Path.Combine(folder, "ThingDoer-v1.0.zip");
        File.Copy(f.Zip(f.Source("update", 2), "update.zip"), path);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        var warned = Assert.Single(manager.State.Jobs);
        Assert.Equal(DownloadStatus.NeedsConfirmation, warned.Status);
        Assert.Null(warned.Error); Assert.Contains("lower version", warned.Warning);
        Assert.Equal(0, failures);
        Assert.Equal(mod.Id, Assert.Single((await f.Library.LoadAsync()).Mods).Id);
        Assert.Equal(job.Id, (await manager.QueueManualUpdateAsync(mod.Id)).Id);
        var persisted = Assert.Single((await new DownloadStore(f.App).LoadAsync()).Jobs);
        Assert.Equal(warned.Warning, persisted.Warning); Assert.Equal(warned.ConfirmationFile, persisted.ConfirmationFile);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConfirmManualUpdateAsync(job.Id));
        Assert.Equal(mod.Id, Assert.Single((await f.Library.LoadAsync()).Mods).Id);
        await manager.RetryAsync(job.Id);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.NeedsConfirmation, Assert.Single(manager.State.Jobs).Status);
        await manager.ConfirmManualUpdateAsync(job.Id);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        Assert.Null(Assert.Single(manager.State.Jobs).Warning);
        Assert.Equal("ThingDoer-v1.0.zip", (await f.Library.LoadAsync()).Mods.Single(item => !item.Superseded).ImportedFileName);
    }

    [Fact]
    public async Task ManualDownloadIgnoresExistingFilesAndUpgradesProfilesWithoutTouchingBrowserFiles()
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Zip(f.Source("old", 1), "ThingDoer-v1.zip"));
        await f.Library.SetPageLinkAsync(old.Id, "https://mods.example/thingdoer");
        var profile = ProfileEditor.Add(ProfileEditor.Create("Default"), old);
        profile = ProfileEditor.AddGroup(profile, "Equipment", [old.Id]);
        profile = ProfileEditor.SetEnabled(profile, old.Id, false);
        await f.Library.SaveProfileAsync(profile);
        var downloads = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(downloads);
        var existing = Path.Combine(downloads, "ThingDoer-v2.zip");
        File.Copy(f.Zip(f.Source("cached", 2), "cached.zip"), existing);
        await using var manager = new ProviderManager(f.Library, new(f.App), [], Path.Combine(f.App, "temp", "downloads"), _ => { });
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([downloads]);
        Assert.False(manager.IsTracked(old)); Assert.Empty(manager.AvailableProviders);
        var job = await manager.QueueManualUpdateAsync(old.Id);
        Assert.Equal(job.Id, (await manager.QueueManualUpdateAsync(old.Id)).Id);
        await Task.Delay(1200);
        Assert.Equal(DownloadStatus.Waiting, Assert.Single(manager.State.Jobs).Status);
        File.Copy(f.Zip(f.Source("new", 3), "new.zip"), existing, overwrite: true);
        var downloaded = await File.ReadAllBytesAsync(existing);
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
        var state = await f.Library.LoadAsync(); var entry = Assert.Single(Assert.Single(state.Profiles).Entries);
        Assert.NotEqual(old.Id, entry.ModId); Assert.False(entry.Enabled); Assert.Equal(profile.Groups[0].Id, entry.GroupId);
        var updated = state.Mods.Single(mod => mod.Id == entry.ModId);
        Assert.Equal("ThingDoer-v2.zip", updated.ImportedFileName); Assert.Equal("ThingDoer-v2", updated.Name);
        Assert.Equal("https://mods.example/thingdoer", updated.PageLink); Assert.Empty(updated.Sources);
        Assert.True(state.Mods.Single(mod => mod.Id == old.Id).Superseded);
        Assert.Equal(downloaded, await File.ReadAllBytesAsync(existing));
        // Stored older versions must not make future filename matching ambiguous.
        var next = await manager.QueueManualUpdateAsync(updated.Id);
        File.Copy(f.Zip(f.Source("newest", 4), "newest.zip"), Path.Combine(downloads, "ThingDoer-v3.zip"));
        await manager.WaitForJobAsync(next.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Complete, manager.State.Jobs.Single(item => item.Id == next.Id).Status);
        Assert.Equal("ThingDoer-v3.zip", (await f.Library.LoadAsync()).Mods.Single(mod => !mod.Superseded).ImportedFileName);
    }

    [Theory]
    [InlineData("ThingDoer-v1.zip", "ThingDoer-v2.zip", "ThingDoer-v3.zip")]
    [InlineData("ThingDoer-1023.zip", "ThingDoer-2291.zip", "ThingDoer-3000.zip")]
    public async Task AmbiguousFilenamesRequireExplicitAttachmentAndLeaveOtherModsAlone(string installed, string otherName, string downloaded)
    {
        using var f = new Fixture();
        var first = await f.Library.ImportAsync(f.Zip(f.Source("one", 1), installed));
        var other = await f.Library.ImportAsync(f.Zip(f.Source("two", 2), otherName));
        await f.Library.SetPageLinkAsync(first.Id, "https://mods.example/one");
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Profile"), first), other);
        await f.Library.SaveProfileAsync(profile);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await using var manager = new ProviderManager(f.Library, new(f.App), [], Path.Combine(f.App, "temp", "downloads"), _ => { });
        await manager.InitializeAsync(); await manager.SetDirectoriesAsync([folder]);
        var job = await manager.QueueManualUpdateAsync(first.Id);
        var archive = f.Zip(f.Source("update", 3), downloaded); File.Copy(archive, Path.Combine(folder, downloaded));
        await manager.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(DownloadStatus.Failed, Assert.Single(manager.State.Jobs).Status);
        Assert.Contains("Attach ZIP", Assert.Single(manager.State.Jobs).Error);
        Assert.Equal(2, (await f.Library.LoadAsync()).Mods.Count);
        await manager.AttachZipAsync(job.Id, archive);
        var state = await f.Library.LoadAsync(); var entries = Assert.Single(state.Profiles).Entries;
        Assert.NotEqual(first.Id, entries[0].ModId); Assert.Equal(other.Id, entries[1].ModId);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(manager.State.Jobs).Status);
    }

    [Fact]
    public async Task InvalidPageAndArchiveMetadataDoNotChangeTheLibrary()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("Mod"));
        foreach (var link in new[] { "javascript:alert(1)", "file:///tmp/test", "https://user@mods.example/test" })
            await Assert.ThrowsAsync<ArgumentException>(() => f.Library.SetPageLinkAsync(mod.Id, link));
        Assert.Null((await f.Library.LoadAsync()).Mods[0].PageLink);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Library.SetDownloadMetadataAsync(mod.Id, "../other.zip", "https://mods.example/mod"));
        Assert.Null((await f.Library.LoadAsync()).Mods[0].ImportedFileName);
    }

    [Fact]
    public async Task SourcesAutomaticallyProvidePageLinksAndExplicitPagesPersist()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("Mod"));
        await f.Library.SetSourcesAsync(mod.Id, [new("nexusmods", "42", "10")]);
        var nexus = (await f.Library.LoadAsync()).Mods[0]; Assert.Equal("https://www.nexusmods.com/helldivers2/mods/42", nexus.PageLink);
        Assert.Equal(nexus.PageLink, ModLinks.PageFor(nexus with { PageLink = null }));
        await f.Library.SetPageLinkAsync(mod.Id, "https://mods.example/mod");
        await f.Library.SetSourcesAsync(mod.Id, [new("github", "owner/repo", "20")]);
        var updated = (await f.Library.LoadAsync()).Mods[0]; Assert.Equal("https://mods.example/mod", updated.PageLink);
        Assert.Equal("https://github.com/owner/repo", ModLinks.PageFor(updated with { PageLink = null }));
    }
}
