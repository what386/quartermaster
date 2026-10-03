using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Providers;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class GuiTests
{
    private static T Page<T>(Fixture fixture, PageKind page) where T : ViewModelBase
    { fixture.Shell.Navigate(page); return Assert.IsType<T>(fixture.Shell.CurrentPage); }

    [AvaloniaFact]
    public async Task ShellRendersAllPagesWithBoundDataAndCapturesPreviews()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
            Assert.Equal("Default", f.Shell.ProfileLabel); Assert.Equal("0", f.Shell.LibraryCount);
            var mods = Page<ModsViewModel>(f, PageKind.Mods);
            f.Dialogs.ZipPath = f.Zip("Impatient Diver"); await mods.ImportZipCommand.ExecuteAsync();
            f.Dialogs.FolderPath = f.OptionsSource(); await mods.ImportFolderCommand.ExecuteAsync();
            var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
            await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
            var routes = new (PageKind Page, Type View)[]
            {
                (PageKind.Mods, typeof(ModsView)), (PageKind.Profiles, typeof(ProfilesView)),
                (PageKind.Providers, typeof(ProvidersView)), (PageKind.Settings, typeof(SettingsView))
            };
            foreach (var (page, view) in routes)
            {
                var menu = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                    button => button.DataContext is NavigationItem item && item.Page == page);
                menu.Command!.Execute(null);
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame); Assert.Equal(page, f.Shell.SelectedNavigation.Page);
                Assert.Contains(window.GetVisualDescendants(), control => control.GetType() == view);
                Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("Welcome to Quartermaster!") == true);
                if (Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } output)
                {
                    Directory.CreateDirectory(output); frame.Save(Path.Combine(output, page + ".png"));
                    File.WriteAllLines(Path.Combine(output, page + ".layout.txt"), window.GetVisualDescendants()
                        .OfType<Control>().Where(c => c is UserControl or ContentControl or ScrollViewer or StackPanel or Grid || c is TextBlock { Classes: var classes } && classes.Contains("pageTitle"))
                        .Select(c => $"{c.GetType().Name} {c.Name} bounds={c.Bounds} position={c.TranslatePoint(new(0, 0), window)} margin={c.Margin}"));
                }
            }
            f.Shell.Navigate(PageKind.Profiles); window.CaptureRenderedFrame()?.Dispose();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Default");
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SidebarSelectsAndPersistsProfileIndependentlyOfMenu()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape");
        await Page<ModsViewModel>(f, PageKind.Mods).ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync();
        profiles.NewProfileName = "Alternate"; await profiles.CreateCommand.ExecuteAsync();
        Assert.Equal("Alternate", f.Shell.ProfileLabel);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.DataContext is NavigationItem item && item.Page == PageKind.Mods);
            menu.Command!.Execute(null); Dispatcher.UIThread.RunJobs();
            var selector = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.DataContext is SidebarProfile item && item.Name == "Default");
            await Assert.IsType<AsyncCommand>(selector.Command).ExecuteAsync();
            Assert.Equal(PageKind.Mods, f.Shell.SelectedNavigation.Page);
            Assert.Equal("Default", profiles.SelectedProfile!.Name);
            Assert.Equal("Default", f.Shell.ProfileLabel);
            Assert.Equal("1 selected for deployment", f.Shell.SelectionSummary);
            Assert.Equal("Default", Assert.Single(f.Shell.SidebarProfiles, item => item.IsActive).Name);
            Assert.Empty(f.Services.Session.Inspection!.Ledger.Files);
            var reopened = new AppServices(f.Data, f.Dialogs, () => []);
            await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal("Default", reopened.Session.ActiveProfile!.Name);
            f.Shell.AddProfileCommand.Execute(null);
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.True(profiles.ShowSettings);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ImportProfileDeploymentAndPurgePersistAndRespectConfirmation()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        f.Dialogs.ZipPath = f.Zip("Cape"); await mods.ImportZipCommand.ExecuteAsync();
        f.Dialogs.ZipPath = f.Zip("Armor"); await mods.ImportZipCommand.ExecuteAsync();
        Assert.Equal(2, mods.Mods.Count);
        mods.Search = "cape"; Assert.Equal("Cape", Assert.Single(mods.Mods).Name); mods.Search = "";
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        Assert.Equal(2, profiles.Entries.Count); Assert.Single(profiles.Conflicts);
        profiles.SelectedMod = profiles.Entries[1]; await profiles.MoveUpCommand.ExecuteAsync();
        Assert.Equal("Armor", profiles.Entries[0].Name);
        await profiles.ToggleCommand.ExecuteAsync(); Assert.Empty(profiles.Conflicts);
        await profiles.ToggleCommand.ExecuteAsync(); Assert.Single(profiles.Conflicts);
        await profiles.MakeActiveCommand.ExecuteAsync();
        profiles.Repair = false;
        var foreign = Path.Combine(f.Game, Fixture.Archive + ".patch_8.stream");
        f.Dialogs.Confirm = false; await profiles.DeployCommand.ExecuteAsync();
        Assert.Null(f.Services.Session.Inspection!.Ledger.SelectionId);
        f.Dialogs.Confirm = true; await profiles.PreviewCommand.ExecuteAsync();
        Assert.Contains("2 patch sets", profiles.PreviewSummary);
        await profiles.DeployCommand.ExecuteAsync();
        Assert.Equal(4, f.Services.Session.Inspection!.Ledger.Files.Count);
        Assert.All(f.Services.Session.Inspection.Ledger.Files, file => Assert.InRange(file.Slot, 0, 1));
        var reopened = new AppServices(f.Data, f.Dialogs, () => []); await reopened.Session.InitializeAsync(CancellationToken.None);
        Assert.Equal(2, reopened.Session.State.Mods.Count); Assert.Equal(2, reopened.Session.ActiveProfile!.Entries.Count);
        Assert.Equal(f.Game, reopened.Session.GameDirectory); Assert.Equal(4, reopened.Session.Inspection!.Ledger.Files.Count);
        f.Dialogs.Confirm = false; await profiles.PurgeCommand.ExecuteAsync(); Assert.True(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
        File.WriteAllBytes(foreign, [0xee]);
        f.Dialogs.Confirm = true; await profiles.PurgeCommand.ExecuteAsync();
        Assert.Empty(f.Services.Session.Inspection!.Ledger.Files); Assert.False(File.Exists(foreign));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
    }

    [AvaloniaFact]
    public async Task ModOptionControlsSaveSelectedVariantToLibrary()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.FolderPath = f.OptionsSource(); await Page<ModsViewModel>(f, PageKind.Mods).ImportFolderCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var option = Assert.Single(profiles.Options!.Options);
            var choice = Assert.Single(window.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == option);
            choice.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, option.ChoiceIndex);
            await profiles.ApplyOptionsCommand.ExecuteAsync();
            var state = f.Services.Session.State; var mod = Assert.Single(state.Mods); var entry = Assert.Single(profiles.SelectedProfile!.Entries);
            Assert.Equal(new[] { "common", "red" }, PatchSelection.Select(mod, entry).Select(p => p.Folder));
            var reopened = new AppServices(f.Data, f.Dialogs, () => []); await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal(1, Assert.Single(reopened.Session.ActiveProfile!.Entries[0].Options).ChoiceIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidSettingsAreReportedAndValidSettingsPersist()
    {
        using var f = new Fixture(discoverGame: false); await f.Shell.InitializeAsync();
        var settings = Page<SettingsViewModel>(f, PageKind.Settings);
        settings.GamePath = Path.Combine(f.Root, "missing"); await settings.SavePathCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Empty(f.Services.Session.GameDirectory);
        settings.GamePath = f.Game; await settings.SavePathCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError); Assert.Equal(f.Game, f.Services.Session.GameDirectory);
        await settings.DiscoverCommand.ExecuteAsync(); Assert.Empty(settings.Installations);
        settings.GamePath = "unsaved edit"; await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Equal("unsaved edit", settings.GamePath);
    }

    [AvaloniaFact]
    public async Task ModifiedDeploymentRequiresPurgeAndErrorAppearsInShell()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape"); await Page<ModsViewModel>(f, PageKind.Mods).ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync(); profiles.Repair = false;
        await profiles.DeployCommand.ExecuteAsync();
        var owned = Path.Combine(f.Game, f.Services.Session.Inspection!.Ledger.Files[0].Name); File.WriteAllBytes(owned, [0xff]);
        await profiles.DeployCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Contains("Purge patches", f.Services.Operations.Message);
        Assert.Equal(new byte[] { 0xff }, File.ReadAllBytes(owned));
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try { window.CaptureRenderedFrame()?.Dispose(); Assert.Equal(f.Services.Operations.Message, window.FindControl<TextBlock>("OperationMessage")!.Text); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RunChecksProfileChangesAndIncompleteManifestBeforeLaunching()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape");
        await Page<ModsViewModel>(f, PageKind.Mods).ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); profiles.Repair = false;
        await profiles.DeployCommand.ExecuteAsync();
        f.Dialogs.Confirmations.Clear();
        await profiles.RunCommand.ExecuteAsync();
        Assert.Equal(1, f.Launches); Assert.Empty(f.Dialogs.Confirmations);
        await profiles.ToggleCommand.ExecuteAsync();
        f.Dialogs.Confirm = false; await profiles.RunCommand.ExecuteAsync();
        Assert.Equal(1, f.Launches);
        Assert.Contains("loadout is not deployed", f.Dialogs.Confirmations[^1].Message);
        f.Dialogs.Confirm = true; await profiles.RunCommand.ExecuteAsync(); Assert.Equal(2, f.Launches);
        profiles.NewProfileName = "B"; await profiles.CreateCommand.ExecuteAsync();
        f.Dialogs.Confirm = false; await profiles.RunCommand.ExecuteAsync(); Assert.Equal(2, f.Launches);
        Assert.Contains("Default", f.Dialogs.Confirmations[^1].Message);
        await File.WriteAllTextAsync(Path.Combine(f.Data, "deployment.lock"), "{broken");
        await profiles.RunCommand.ExecuteAsync(); Assert.Equal(2, f.Launches);
        Assert.Contains("Purge patches", f.Dialogs.Confirmations[^1].Message);
        Assert.True(f.Services.Session.Inspection!.NeedsPurge);
        f.Dialogs.Confirm = true; await profiles.PurgeCommand.ExecuteAsync();
        Assert.False(f.Services.Session.Inspection!.NeedsPurge);
        Assert.False(File.Exists(Path.Combine(f.Data, "deployment.lock")));
        Assert.True(File.Exists(Path.Combine(f.Data, "library.json")));
        Assert.True(File.Exists(Path.Combine(f.Data, "profiles.json")));
        foreach (var line in File.ReadAllLines(Path.Combine(f.Data, "log.jsonl")))
        {
            using var entry = global::System.Text.Json.JsonDocument.Parse(line);
            Assert.True(entry.RootElement.TryGetProperty("operation", out _));
        }
    }

    [AvaloniaFact]
    public async Task RowEnableControlPersistsAndUpdatesCollisionBadges()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape"); var mods = Page<ModsViewModel>(f, PageKind.Mods);
        await mods.ImportZipCommand.ExecuteAsync();
        f.Dialogs.ZipPath = f.Zip("Armor"); await mods.ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        Assert.All(profiles.Entries, row => Assert.True(row.HasConflict));
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var row = profiles.Entries[0];
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => b.Command == row.EnableCommand);
            await Assert.IsType<AsyncCommand>(button.Command).ExecuteAsync();
            Assert.False(profiles.Entries[0].IsEnabled);
            Assert.All(profiles.Entries, entry => Assert.False(entry.HasConflict));
            Assert.Equal("0", f.Shell.CollisionCount);
            var reopened = new AppServices(f.Data, f.Dialogs, () => []);
            await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.False(reopened.Session.ActiveProfile!.Entries[0].Enabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWindowCancelsAndWaitsForOperationToFinish()
    {
        using var f = new Fixture();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        var operation = f.Services.Operations.RunAsync("Waiting", ct => Task.Delay(Timeout.Infinite, ct));
        window.Close();
        await operation; await f.Services.Operations.WhenIdle;
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible); Assert.False(f.Services.Operations.IsBusy);
        Assert.False(f.Services.Operations.IsError);
    }

    [AvaloniaFact]
    public async Task BusyOperationsPreventConcurrentCommandsAndCanBeCancelled()
    {
        var operations = new OperationState(); var calls = 0;
        var other = operations.CreateCommand("Other", _ => { calls++; return Task.CompletedTask; });
        var running = operations.RunAsync("Waiting", ct => Task.Delay(Timeout.Infinite, ct));
        Assert.True(operations.IsBusy); Assert.False(other.CanExecute(null));
        await other.ExecuteAsync(); Assert.Equal(0, calls);
        operations.CancelCommand.Execute(null); await running; await operations.WhenIdle;
        Assert.False(operations.IsBusy); Assert.False(operations.IsError); Assert.Contains("cancelled", operations.Message);
        Assert.True(other.CanExecute(null)); await other.ExecuteAsync(); Assert.Equal(1, calls);
    }
}
