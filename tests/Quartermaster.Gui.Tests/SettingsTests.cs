using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Settings;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class SettingsTests
{
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EchelonDetectionPreselectsOnlyAnUnambiguousMatch(int count)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var paths = Enumerable.Range(0, count).Select(i => Path.Combine(f.Root, "Echelon-" + i)).ToArray();
        var settings = new SettingsViewModel(f.Services, discoverEchelonDirectories: _ => Task.FromResult<IReadOnlyList<string>>(paths));
        var view = new SettingsView { DataContext = settings };
        await settings.RefreshEchelonDirectoriesAsync(); Dispatcher.UIThread.RunJobs();
        Assert.Equal(paths, settings.EchelonDirectories);
        Assert.Equal(count == 1 ? paths[0] : "", settings.EchelonDirectory);
        Assert.Equal(count == 1, settings.ImportEchelonCommand.CanExecute(null));
        var selector = view.FindControl<ComboBox>("EchelonDirectorySelector")!;
        Assert.Equal(count > 1, selector.IsVisible);
        if (count > 1)
        {
            Assert.Null(selector.SelectedItem);
            selector.SelectedItem = paths[1]; Dispatcher.UIThread.RunJobs();
            Assert.Equal(paths[1], settings.EchelonDirectory); Assert.True(settings.ImportEchelonCommand.CanExecute(null));
        }
    }

    [AvaloniaFact]
    public async Task EchelonDetectionStartsWhenSettingsOpens()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var path = Path.Combine(f.Root, "detected");
        var settings = new SettingsViewModel(f.Services, discoverEchelonDirectories: _ => Task.FromResult<IReadOnlyList<string>>([path]));
        var window = new Window { Content = new SettingsView { DataContext = settings } };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(path, settings.EchelonDirectory);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task EchelonDetectionDoesNotOverwriteBrowsingOrApplyCancelledResults()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var pending = new TaskCompletionSource<IReadOnlyList<string>>();
        var settings = new SettingsViewModel(f.Services, discoverEchelonDirectories: _ => pending.Task);
        var refresh = settings.RefreshEchelonDirectoriesAsync();
        f.Dialogs.FolderPath = Path.Combine(f.Root, "backup");
        await settings.BrowseEchelonCommand.ExecuteAsync();
        pending.SetResult([Path.Combine(f.Root, "detected")]); await refresh;
        Assert.Equal(f.Dialogs.FolderPath, settings.EchelonDirectory);
        settings.SelectedEchelonDirectory = null; Assert.Equal(f.Dialogs.FolderPath, settings.EchelonDirectory);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = new SettingsViewModel(f.Services, discoverEchelonDirectories: _ => Task.FromResult<IReadOnlyList<string>>(["cancelled"]));
        await cancelled.RefreshEchelonDirectoriesAsync(cancellation.Token);
        Assert.Empty(cancelled.EchelonDirectories); Assert.Equal("", cancelled.EchelonDirectory);
    }

    [AvaloniaFact]
    public async Task EchelonImportConfirmsCopiesModsAndReloadsProfiles()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var root = Path.Combine(f.Root, "Echelon"); Directory.CreateDirectory(Path.Combine(root, "mods"));
        var modPath = f.Source("echelon-source");
        Directory.Move(modPath, Path.Combine(root, "mods", "sample"));
        var file = Directory.GetFiles(Path.Combine(root, "mods", "sample")).Single(path => System.Text.RegularExpressions.Regex.IsMatch(path, @"\.patch_\d+$"));
        var data = new
        {
            version = 2,
            active_profile = "profile",
            library = new Dictionary<string, object> { ["sample"] = new { id = "sample", name = "Imported", sets = new[] { new { files = new Dictionary<string, string> { [""] = Path.GetFileName(file) } } } } },
            profiles = new[] { new { id = "profile", name = "Echelon profile", entries = new[] { new { type = "mod", id = "sample", enabled = true } } } }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "state.json"), System.Text.Json.JsonSerializer.Serialize(data));
        f.Dialogs.FolderPath = root;
        await settings.BrowseEchelonCommand.ExecuteAsync(); Assert.Equal(root, settings.EchelonDirectory);
        settings.Search = "Echelon"; Assert.True(settings.ShowImports);
        f.Dialogs.Confirm = false;
        await settings.ImportEchelonCommand.ExecuteAsync(); Assert.Empty(f.Services.Session.State.Mods);
        Assert.Equal("Import Echelon profiles", Assert.Single(f.Dialogs.Confirmations).Title);
        f.Dialogs.Confirm = true;
        await settings.ImportEchelonCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal("Imported", Assert.Single(f.Services.Session.State.Mods).Name);
        Assert.Equal("Echelon profile", f.Services.Session.ActiveProfile!.Name);
        settings.EchelonDirectory = ""; Assert.False(settings.ImportEchelonCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CredentialInstructionsOpenTheCorrespondingAccountSettings()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var nexus = Assert.Single(window.GetVisualDescendants().OfType<NexusSettingsView>());
            var github = Assert.Single(window.GetVisualDescendants().OfType<GitHubSettingsView>());
            var nexusLink = nexus.FindControl<HyperlinkButton>("NexusKeySettingsLink")!;
            var githubLink = github.FindControl<HyperlinkButton>("GitHubTokenSettingsLink")!;
            await Assert.IsType<Quartermaster.Gui.Shared.AsyncCommand>(nexusLink.Command).ExecuteAsync();
            await Assert.IsType<Quartermaster.Gui.Shared.AsyncCommand>(githubLink.Command).ExecuteAsync();
            Assert.Equal(new[]
            {
                new Uri("https://www.nexusmods.com/settings/api-keys"),
                new Uri("https://github.com/settings/personal-access-tokens")
            }, f.BrowserRequests);
            Assert.False(f.Services.Operations.IsError);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DownloadsHaveTheirOwnSectionAndSaveWithoutProviderCredentials()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            var input = view.FindControl<TextBox>("DownloadFolderInput")!;
            Assert.Contains(view.FindControl<Border>("DownloadsSettingsSection")!, input.GetVisualAncestors());
            Assert.DoesNotContain(input.GetVisualAncestors(), control => control is NexusSettingsView);
            settings.Search = "download"; Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Border>("DownloadsSettingsSection")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("ProviderSettingsSection")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("AppSettingsSection")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("GameSettingsSection")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Border>("AppInformationSection")!.IsEffectivelyVisible);
            var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
            input.Text = folder; Dispatcher.UIThread.RunJobs();
            await settings.SaveCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError);
            Assert.Equal(folder, Assert.Single(f.Services.Providers.State.Directories));
            Assert.Null(await f.Services.Keys.GetAsync("nexusmods"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("app settings", "AppSettingsSection")]
    [InlineData("game and deployment", "GameSettingsSection")]
    [InlineData("providers", "ProviderSettingsSection")]
    [InlineData("app information", "AppInformationSection")]
    [InlineData("imports", "ImportsSettingsSection")]
    [InlineData("storage", "StorageSettingsSection")]
    [InlineData("cleanup", "StorageSettingsSection")]
    [InlineData("no matching settings here", null)]
    public async Task SearchShowsOnlyMatchingFunctionalSections(string search, string? expected)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            settings.Search = search; Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            foreach (var name in new[] { "AppSettingsSection", "GameSettingsSection", "DownloadsSettingsSection", "StorageSettingsSection", "ProviderSettingsSection", "AppInformationSection", "ImportsSettingsSection" })
                Assert.Equal(name == expected, view.FindControl<Border>(name)!.IsEffectivelyVisible);
            Assert.Equal(expected is not null, settings.HasMatches);
        }
        finally { window.Close(); }
    }
}
