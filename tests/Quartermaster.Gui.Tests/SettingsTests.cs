using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Settings;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class SettingsTests
{
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
            foreach (var name in new[] { "AppSettingsSection", "GameSettingsSection", "DownloadsSettingsSection", "ProviderSettingsSection", "AppInformationSection" })
                Assert.Equal(name == expected, view.FindControl<Border>(name)!.IsEffectivelyVisible);
            Assert.Equal(expected is not null, settings.HasMatches);
        }
        finally { window.Close(); }
    }
}
