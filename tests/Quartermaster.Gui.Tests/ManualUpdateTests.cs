using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Downloads;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Downloads;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ManualUpdateTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticallyImportedZipFinishesTheCheckEvenWithIdenticalContents(bool identical)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var initial = f.Zip("ThingDoer-v1", 1);
        await f.Services.Session.ImportAsync(initial, CancellationToken.None, f.Services.Session.ActiveProfile!.Id);
        var original = Assert.Single(f.Services.Session.State.Mods);
        await f.Services.Library.SetPageLinkAsync(original.Id, "https://mods.example/mod");
        await f.Services.Session.ImportAsync(f.Zip("Other mod", 3), CancellationToken.None);
        var other = f.Services.Session.State.Mods.Single(mod => mod.Id != original.Id);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        f.Services.Downloads.ShowManualChecks();
        await f.Services.Downloads.ManualChecks.Single(row => row.Name == original.Name).OpenCommand.ExecuteAsync();
        Dispatcher.UIThread.RunJobs();
        var job = Assert.Single(f.Services.Providers.State.Jobs);
        Assert.True(f.Shell.ShowDownloadProgress);
        File.Copy(identical ? initial : f.Zip("ThingDoer-v2", 2), Path.Combine(folder, "ThingDoer-v2.zip"));
        await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(DownloadStatus.Complete, Assert.Single(f.Services.Providers.State.Jobs).Status);
        Assert.False(f.Shell.ShowDownloadProgress);
        Assert.Equal(other.Name, Assert.Single(f.Services.Downloads.ManualChecks).Name);
        await f.Services.Session.ReloadAsync(CancellationToken.None); Dispatcher.UIThread.RunJobs();
        Assert.Equal(other.Name, Assert.Single(f.Services.Downloads.ManualChecks).Name);
        var updatedId = Assert.Single(f.Services.Session.ActiveProfile!.Entries).ModId;
        Assert.Equal(identical, updatedId == original.Id);
        f.Services.Downloads.ShowManualChecks(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, f.Services.Downloads.ManualChecks.Count);
    }

    [AvaloniaFact]
    public async Task LowerVersionWarningOffersImportAnywayAndUpdatesTheProfile()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Zip("ThingDoer-v3", 1), CancellationToken.None, f.Services.Session.ActiveProfile!.Id);
        var original = Assert.Single(f.Services.Session.State.Mods);
        await f.Services.Library.SetPageLinkAsync(original.Id, "https://mods.example/mod");
        f.Services.Downloads.ShowManualChecks();
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        var job = await f.Services.Providers.QueueManualUpdateAsync(original.Id);
        File.Copy(f.Zip("ThingDoer-v1.0", 2), Path.Combine(folder, "ThingDoer-v1.0.zip"));
        await f.Services.Providers.WaitForJobAsync(job.Id).WaitAsync(TimeSpan.FromSeconds(8));
        Dispatcher.UIThread.RunJobs();
        var row = Assert.Single(f.Services.Downloads.Jobs);
        Assert.True(row.CanConfirm); Assert.Contains("lower version", row.Status);
        var manualRow = Assert.Single(f.Services.Downloads.ManualChecks);
        Assert.True(manualRow.CanConfirm); Assert.Contains("lower version", manualRow.Warning);
        Assert.False(f.Services.Operations.IsError);
        Assert.Equal(original.Id, Assert.Single(f.Services.Session.ActiveProfile!.Entries).ModId);
        await manualRow.ConfirmCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.False(row.CanConfirm); Assert.False(f.Services.Operations.IsError);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(f.Services.Providers.State.Jobs).Status);
        Assert.NotEqual(original.Id, Assert.Single(f.Services.Session.ActiveProfile!.Entries).ModId);
    }

    [AvaloniaFact]
    public async Task PageLinksCanBeSavedOpenedAndShownInTheManualCheckQueue()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Zip("Homing stim"), CancellationToken.None, f.Services.Session.ActiveProfile!.Id);
        f.Shell.NavigationItems.Single(item => item.Page == PageKind.Mods).OpenCommand.Execute(null);
        var library = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
        var details = library.Details!;
        details.PageLink = "https://mods.example/homing";
        await details.SavePageCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.Equal(details.PageLink, Assert.Single(f.Services.Session.State.Mods).PageLink);
        await details.OpenPageCommand.ExecuteAsync(); Assert.Equal(new Uri(details.PageLink), Assert.Single(f.BrowserRequests));
        var profile = Assert.Single(f.Services.Session.State.Profiles);
        f.Shell.NavigationItems.Single(item => item.Page == PageKind.Profiles).OpenCommand.Execute(null);
        var profiles = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        Assert.Equal(details.PageLink, profiles.Details!.PageLink);
        await profiles.CheckUpdatesCommand.ExecuteAsync();
        Assert.Equal(PageKind.Downloads, f.Shell.SelectedNavigation.Page);
        Assert.Equal(1, f.Services.Downloads.SelectedTab);
        Assert.Single(f.Services.Downloads.ManualChecks);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Single(window.GetVisualDescendants().OfType<ManualChecksView>());
            var row = Assert.Single(f.Services.Downloads.ManualChecks);
            await row.OpenCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, f.BrowserRequests.Count);
            Assert.Equal(profile.Entries[0].ModId, Assert.Single(f.Services.Providers.State.Jobs).ReplacesModId);
            var overlay = window.FindControl<Border>("DownloadProgressOverlay")!;
            Assert.True(overlay.IsVisible);
            Assert.Equal("Waiting for 1 matching ZIP", f.Services.Downloads.PendingSummary);
            Assert.Equal("Watching your download folder for matching ZIPs.", f.Services.Downloads.PendingExplanation);
            f.Services.Downloads.SelectedTab = 0; Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsVisible);
            f.Services.Downloads.SelectedTab = 1; Dispatcher.UIThread.RunJobs();
            Assert.True(overlay.IsVisible);
            f.Shell.NavigationItems.Single(item => item.Page == PageKind.Mods).OpenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); Assert.True(overlay.IsVisible);
            await row.DoneCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(f.Services.Downloads.ManualChecks); Assert.Empty(f.Services.Providers.State.Jobs);
            Assert.False(overlay.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ManualAttachmentReplacesProfileModAndKeepsPageWithoutOpeningTheBrowser()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Zip("Old", 1), CancellationToken.None, f.Services.Session.ActiveProfile!.Id);
        var original = Assert.Single(f.Services.Session.State.Mods);
        await f.Services.Library.SetPageLinkAsync(original.Id, "https://mods.example/my-mod");
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        f.Services.Downloads.ShowManualChecks();
        f.Dialogs.ZipPath = f.Zip("Renamed update", 2);
        await Assert.Single(f.Services.Downloads.ManualChecks).AttachCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Services.Operations.IsError); Assert.Empty(f.BrowserRequests);
        Assert.Equal(DownloadStatus.Complete, Assert.Single(f.Services.Providers.State.Jobs).Status);
        var entry = Assert.Single(f.Services.Session.ActiveProfile!.Entries);
        Assert.NotEqual(original.Id, entry.ModId);
        var updated = f.Services.Session.State.Mods.Single(mod => mod.Id == entry.ModId);
        Assert.Equal("https://mods.example/my-mod", updated.PageLink);
        Assert.Equal("Renamed update.zip", updated.ImportedFileName);
        Assert.Empty(f.Services.Downloads.ManualChecks);
    }

    [AvaloniaFact]
    public async Task UnknownPagePromptCanBeCancelledWithoutStartingAWatchOrShowingAnError()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Untracked"), CancellationToken.None);
        f.Services.Downloads.ShowManualChecks();
        await Assert.Single(f.Services.Downloads.ManualChecks).OpenCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError); Assert.Empty(f.Services.Providers.State.Jobs); Assert.Empty(f.BrowserRequests);
    }
}
