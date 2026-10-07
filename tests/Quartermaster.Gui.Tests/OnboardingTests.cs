using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Onboarding;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class OnboardingTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SingleStepButtonSkipsEmptyOptionalFieldsAndContinuesWithValues(bool foundGame)
    {
        using var f = new Fixture(discoverGame: foundGame); await f.Shell.InitializeAsync();
        using var model = new SetupViewModel(f.Services, CancellationToken.None);
        var dialog = new SetupDialog(model);
        var window = new Window { Content = dialog, Width = 580, Height = 440 }; window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var skip = dialog.FindControl<Button>("SkipTourButton")!;
            var next = dialog.FindControl<Button>("NextStepButton")!;
            var skipSetup = dialog.FindControl<Button>("SkipSetupButton")!;
            Assert.False(skip.IsEffectivelyVisible);
            Assert.Equal(foundGame ? "Continue" : "Skip", next.Content);
            Assert.Equal(!foundGame, skipSetup.IsEffectivelyVisible);
            Assert.False(model.SkipCommand.CanExecute(null));
            if (foundGame)
            {
                model.GamePath = "";
                model.SkipCommand.Execute(null); model.SkipSetupCommand.Execute(null);
                Assert.True(model.IsGame);
                await model.NextCommand.ExecuteAsync(); Assert.True(model.IsGame); Assert.NotEmpty(model.Error);
                model.GamePath = f.Game; await model.NextCommand.ExecuteAsync();
            }
            else
            {
                dialog.FindControl<TextBox>("GameFolderInput")!.Text = "invalid";
                Dispatcher.UIThread.RunJobs(); Assert.Equal("Continue", next.Content);
                await model.NextCommand.ExecuteAsync(); Assert.True(model.IsGame); Assert.NotEmpty(model.Error);
                dialog.FindControl<TextBox>("GameFolderInput")!.Text = "   ";
                Dispatcher.UIThread.RunJobs(); Assert.Equal("Skip", next.Content);
                await model.NextCommand.ExecuteAsync();
            }
            Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsUpdates); Assert.False(skip.IsEffectivelyVisible); Assert.False(skipSetup.IsEffectivelyVisible);
            Assert.Equal("Continue", next.Content);
            await model.NextCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsNexus); Assert.False(skip.IsEffectivelyVisible); Assert.Equal("Skip", next.Content);
            dialog.FindControl<TextBox>("NexusKeyInput")!.Text = "new-key";
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Continue", next.Content);
            dialog.FindControl<TextBox>("NexusKeyInput")!.Text = "";
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Skip", next.Content);
            await model.NextCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsGitHub); Assert.Equal("Skip", next.Content);
            await model.NextCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(model.IsDownloads); Assert.False(skip.IsEffectivelyVisible);
            model.DownloadFolder = ""; Dispatcher.UIThread.RunJobs(); Assert.Equal("Continue", next.Content);
            await model.NextCommand.ExecuteAsync(); Assert.True(model.IsDownloads);
            model.DownloadFolder = Path.Combine(f.Root, "downloads"); await model.NextCommand.ExecuteAsync();
            Dispatcher.UIThread.RunJobs(); Assert.True(model.IsReady); Assert.True(skip.IsEffectivelyVisible); Assert.Equal("Take the tour", next.Content);
        }
        finally { window.Close(); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    [AvaloniaFact]
    public async Task FirstRunIsRememberedAndSettingsCanReplayIt()
    {
        using var f = new Fixture();
        await f.Shell.InitializeAsync(); Assert.Equal(1, f.Dialogs.OnboardingPrompts);
        Assert.True((await new SettingsStore(f.Data).LoadAsync(CancellationToken.None)).OnboardingCompleted);
        await f.Shell.InitializeAsync(); Assert.Equal(1, f.Dialogs.OnboardingPrompts);
        f.Shell.Navigate(PageKind.Settings);
        var settings = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        await settings.ReplayOnboardingCommand.ExecuteAsync(); Assert.Equal(2, f.Dialogs.OnboardingPrompts);
    }

    [AvaloniaFact]
    public async Task OptionalStepsCanBeSkippedWithoutChangingPreferencesOrSavedKeys()
    {
        using var f = new Fixture(discoverGame: false); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "existing-nexus");
        await f.Services.Keys.SetAsync("github", "existing-github");
        using var model = new SetupViewModel(f.Services, CancellationToken.None); await model.InitializeAsync();
        Assert.Matches(@"^\*+$", model.NexusApiKey); Assert.Matches(@"^\*+$", model.GitHubToken);
        model.GamePath = ""; model.NexusApiKey = ""; model.GitHubToken = "";
        await model.NextCommand.ExecuteAsync();
        await model.NextCommand.ExecuteAsync();
        await model.NextCommand.ExecuteAsync(); await model.NextCommand.ExecuteAsync();
        await model.NextCommand.ExecuteAsync();
        Assert.True(model.IsReady); Assert.Empty(model.Error); Assert.Empty(f.Services.Session.GameDirectory);
        Assert.False(f.Services.Session.Settings.AllowAutomaticUpdate);
        Assert.Equal("existing-nexus", await f.Services.Keys.GetAsync("nexusmods"));
        Assert.Equal("existing-github", await f.Services.Keys.GetAsync("github"));
    }

    [AvaloniaFact]
    public async Task SavedMasksArePreservedAndRemovalButtonsCommitOnlyOnContinue()
    {
        using var api = new HttpClient(new Handler(_ => throw new InvalidOperationException("Saved masks must not be validated.")));
        using var f = new Fixture(nexusApi: api, githubApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "existing-nexus");
        await f.Services.Keys.SetAsync("github", "existing-github");
        using var model = new SetupViewModel(f.Services, CancellationToken.None); await model.InitializeAsync();
        var dialog = new SetupDialog(model);
        await model.NextCommand.ExecuteAsync(); await model.NextCommand.ExecuteAsync();
        var removeNexus = dialog.FindControl<Button>("RemoveNexusKeyButton")!;
        Assert.Matches(@"^\*+$", model.NexusApiKey);
        removeNexus.Command!.Execute(null);
        Assert.Empty(model.NexusApiKey); Assert.True(model.RemoveNexusKey);
        Assert.Equal("existing-nexus", await f.Services.Keys.GetAsync("nexusmods"));
        Assert.Equal("Continue", model.NextLabel);
        removeNexus.Command.Execute(null);
        Assert.Matches(@"^\*+$", model.NexusApiKey); Assert.False(model.RemoveNexusKey);
        await model.NextCommand.ExecuteAsync(); Assert.True(model.IsGitHub); Assert.Empty(model.Error);
        Assert.Equal("existing-nexus", await f.Services.Keys.GetAsync("nexusmods"));
        await model.NextCommand.ExecuteAsync(); Assert.True(model.IsDownloads); Assert.Empty(model.Error);
        Assert.Equal("existing-github", await f.Services.Keys.GetAsync("github"));
        model.BackCommand.Execute(null);
        dialog.FindControl<Button>("RemoveGitHubTokenButton")!.Command!.Execute(null);
        Assert.Equal("existing-github", await f.Services.Keys.GetAsync("github"));
        await model.NextCommand.ExecuteAsync(); Assert.Null(await f.Services.Keys.GetAsync("github"));
        Assert.False(model.HasSavedGitHubToken); Assert.Empty(model.GitHubToken);
        model.BackCommand.Execute(null); model.BackCommand.Execute(null);
        removeNexus.Command.Execute(null); await model.NextCommand.ExecuteAsync();
        Assert.Null(await f.Services.Keys.GetAsync("nexusmods")); Assert.False(model.HasSavedNexusKey);
    }

    [AvaloniaFact]
    public async Task SetupSavesValidatedChoicesAndLeavesInvalidInputOnItsStep()
    {
        using var nexus = new HttpClient(new Handler(_ => Json("""{"user_id":7,"name":"Diver","is_premium":false}""")));
        using var github = new HttpClient(new Handler(_ => Json("""{"login":"diver"}""")));
        using var f = new Fixture(discoverGame: false, nexusApi: nexus, githubApi: github); await f.Shell.InitializeAsync();
        using var model = new SetupViewModel(f.Services, CancellationToken.None);
        model.GamePath = Path.Combine(f.Root, "missing"); await model.NextCommand.ExecuteAsync();
        Assert.True(model.IsGame); Assert.NotEmpty(model.Error);
        model.GamePath = f.Game; await model.NextCommand.ExecuteAsync(); Assert.Equal(f.Game, f.Services.Session.GameDirectory);
        model.AllowAutomaticUpdate = true; await model.NextCommand.ExecuteAsync();
        Assert.True(f.Services.Session.Settings.AllowAutomaticUpdate);
        model.NexusApiKey = "nexus-key"; await model.NextCommand.ExecuteAsync();
        Assert.True(model.IsGitHub); Assert.Matches(@"^\*+$", model.NexusApiKey);
        model.GitHubToken = "github-token"; await model.NextCommand.ExecuteAsync(); Assert.True(model.IsDownloads);
        model.DownloadFolder = "relative"; await model.NextCommand.ExecuteAsync(); Assert.True(model.IsDownloads); Assert.NotEmpty(model.Error);
        model.DownloadFolder = Path.Combine(f.Root, "downloads"); await model.NextCommand.ExecuteAsync(); Assert.True(model.IsReady);
        Assert.Equal(model.DownloadFolder, Assert.Single(f.Services.Providers.State.Directories));
        Assert.Equal("nexus-key", await f.Services.Keys.GetAsync("nexusmods")); Assert.Equal("github-token", await f.Services.Keys.GetAsync("github"));
        var stored = await File.ReadAllTextAsync(Path.Combine(f.Data, "settings.json"));
        Assert.DoesNotContain("nexus-key", stored); Assert.DoesNotContain("github-token", stored);
    }

    [AvaloniaFact]
    public async Task RejectedKeysCanBeSkippedAndDoNotOverwriteSavedCredentials()
    {
        using var api = new HttpClient(new Handler(_ => Json("{}", HttpStatusCode.Unauthorized)));
        using var f = new Fixture(nexusApi: api, githubApi: api); await f.Shell.InitializeAsync();
        await f.Services.Keys.SetAsync("nexusmods", "saved");
        using var model = new SetupViewModel(f.Services, CancellationToken.None);
        await model.NextCommand.ExecuteAsync(); await model.NextCommand.ExecuteAsync();
        model.NexusApiKey = "bad-key"; await model.NextCommand.ExecuteAsync(); Assert.True(model.IsNexus); Assert.NotEmpty(model.Error);
        Assert.Equal("saved", await f.Services.Keys.GetAsync("nexusmods"));
        model.NexusApiKey = ""; await model.NextCommand.ExecuteAsync(); model.GitHubToken = "bad-token";
        await model.NextCommand.ExecuteAsync(); Assert.True(model.IsGitHub); Assert.NotEmpty(model.Error);
        Assert.Null(await f.Services.Keys.GetAsync("github")); model.GitHubToken = "";
        await model.NextCommand.ExecuteAsync(); Assert.True(model.IsDownloads);
    }

    [AvaloniaFact]
    public async Task TourVisitsRealPagesHighlightsNavigationAndClearsItsHighlights()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        f.Dialogs.OnboardingHandler = _ => Task.FromResult(SetupOutcome.TakeTour);
        var run = f.Shell.Onboarding.RunAsync();
        try
        {
            // Let the settings receipt and provider refresh finish before the first card appears.
            while (!f.Shell.Onboarding.IsTourVisible && !run.IsCompleted) await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
            var card = Assert.Single(window.GetVisualDescendants().OfType<TourView>());
            Assert.True(card.IsEffectivelyVisible);
            Assert.Equal(PageKind.Mods, f.Shell.SelectedNavigation.Page);
            Assert.True(f.Shell.LibraryNavigation.IsTourTarget);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(Path.GetTempPath(), "quartermaster-onboarding-tour.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            var add = Assert.Single(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "AddModButton");
            var addOrigin = add.TranslatePoint(default, window)!.Value;
            var firstOrigin = card.TranslatePoint(default, window)!.Value;
            Assert.InRange(firstOrigin.Y - (addOrigin.Y + add.Bounds.Height), 10, 14);
            window.Width = 1040; window.Height = 680;
            window.CaptureRenderedFrame()?.Dispose();
            var resizedOrigin = card.TranslatePoint(default, window)!.Value;
            Assert.True(resizedOrigin.X < firstOrigin.X);
            Assert.True(resizedOrigin.X + card.Bounds.Width <= window.ClientSize.Width - 10);
            f.Shell.Onboarding.NextCommand.Execute(null); Assert.True(f.Shell.Onboarding.IsProfileStep);
            Assert.Contains("tourTarget", window.FindControl<Button>("AddProfileButton")!.Classes);
            window.CaptureRenderedFrame()?.Dispose();
            var profileOrigin = card.TranslatePoint(default, window)!.Value;
            var create = window.FindControl<Button>("AddProfileButton")!;
            Assert.InRange(profileOrigin.X - (create.TranslatePoint(default, window)!.Value.X + create.Bounds.Width), 10, 14);
            f.Shell.Onboarding.BackCommand.Execute(null); Assert.Equal(PageKind.Mods, f.Shell.SelectedNavigation.Page);
            foreach (var page in new[] { PageKind.Profiles, PageKind.Search, PageKind.ManualChecks, PageKind.Downloads, PageKind.Settings })
            {
                f.Shell.Onboarding.NextCommand.Execute(null); Assert.Equal(page, f.Shell.SelectedNavigation.Page);
                window.CaptureRenderedFrame()?.Dispose();
                var origin = card.TranslatePoint(default, window)!.Value;
                Assert.InRange(origin.Y, 10, window.ClientSize.Height - card.Bounds.Height - 10);
                Assert.InRange(origin.X, 10, 100);
            }
            Assert.Equal("Finish", f.Shell.Onboarding.NextLabel);
            f.Shell.Onboarding.NextCommand.Execute(null); await run;
            Assert.False(f.Shell.Onboarding.IsTourVisible); Assert.All(f.Shell.NavigationItems, item => Assert.False(item.IsTourTarget));
        }
        finally { f.Shell.Onboarding.SkipCommand.Execute(null); window.Close(); }
    }

    [AvaloniaFact]
    public async Task SetupDialogMasksKeysAndOffersSkipOnEveryOptionalStep()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        using var model = new SetupViewModel(f.Services, CancellationToken.None);
        var dialog = new SetupDialog(model);
        var window = new Window { Content = dialog, Width = 580, Height = 440 }; window.Show();
        try
        {
            await model.NextCommand.ExecuteAsync(); await model.NextCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<TextBox>("NexusKeyInput")!.IsEffectivelyVisible);
            Assert.Equal('*', dialog.FindControl<TextBox>("NexusKeyInput")!.PasswordChar);
            Assert.Equal('*', dialog.FindControl<TextBox>("GitHubTokenInput")!.PasswordChar);
            using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(Path.GetTempPath(), "quartermaster-onboarding-setup.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            SetupOutcome? outcome = null; dialog.Completed += value => outcome = Assert.IsType<SetupOutcome>(value);
            model.SkipSetupCommand.Execute(null); Assert.Equal(SetupOutcome.TakeTour, outcome);
        }
        finally { window.Close(); }
    }
}
