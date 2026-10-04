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
                if (page == PageKind.Profiles)
                {
                    var selector = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                        button => button.DataContext is SidebarProfile { Name: "Default" });
                    await Assert.IsType<AsyncCommand>(selector.Command).ExecuteAsync();
                }
                else
                {
                    var menu = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                        button => button.DataContext is NavigationItem item && item.Page == page);
                    menu.Command!.Execute(null);
                }
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
    public async Task ProgressFloatsAtBottomCenterWithoutMovingPageAndCanCancel()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        var started = new TaskCompletionSource();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var host = window.FindControl<ContentControl>("PageHost")!;
            var originalBounds = host.Bounds;
            var operation = f.Services.Operations.RunAsync("Repatching mods", async ct =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });
            await started.Task;
            window.CaptureRenderedFrame()?.Dispose();
            var overlay = window.FindControl<Border>("ProgressOverlay")!;
            Assert.True(overlay.IsVisible); Assert.Equal(originalBounds, host.Bounds);
            var origin = overlay.TranslatePoint(new Point(0, 0), window)!.Value;
            Assert.Equal(window.Bounds.Width / 2, origin.X + overlay.Bounds.Width / 2, 1);
            Assert.True(origin.Y > window.Bounds.Height / 2);
            Assert.Equal(24, window.Bounds.Height - origin.Y - overlay.Bounds.Height, 1);
            var cancel = Assert.Single(overlay.GetVisualDescendants().OfType<Button>());
            Assert.Same(f.Services.Operations.CancelCommand, cancel.Command); cancel.Command!.Execute(null);
            await operation; window.CaptureRenderedFrame()?.Dispose();
            Assert.False(overlay.IsVisible); Assert.Equal(originalBounds, host.Bounds);
        }
        finally { f.Services.Operations.CancelCommand.Execute(null); await f.Services.Operations.WhenIdle; window.Close(); }
    }

    [AvaloniaFact]
    public async Task DoubleClickingProfileUsesDeploymentConfirmationAndWaitsForSelection()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        var target = Assert.Single(f.Shell.SidebarProfiles);
        f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.DataContext, target));
            f.Dialogs.Confirmations.Clear(); f.Dialogs.Confirm = false;
            await target.SelectCommand.ExecuteAsync(); window.CaptureRenderedFrame()?.Dispose();
            var point = button.TranslatePoint(new Point(24, 24), window)!.Value;
            window.MouseMove(point);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            await f.Services.Operations.WhenIdle; Dispatcher.UIThread.RunJobs();
            Assert.Equal(target.Profile.Id, f.Services.Session.ActiveProfile!.Id);
            Assert.Contains(f.Dialogs.Confirmations, c => c.Title == "Deploy profile" && c.Message.Contains(target.Name));
            Assert.False(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
            f.Dialogs.Confirm = true; f.Dialogs.Confirmations.Clear();
            await f.Shell.SidebarProfiles.Single(p => p.Name == "Other").SelectCommand.ExecuteAsync();
            var selection = target.SelectCommand.ExecuteAsync();
            var deploy = f.Shell.DeployProfileAsync(target.Profile.Id);
            await selection; await deploy;
            Assert.Single(f.Dialogs.Confirmations, c => c.Title == "Deploy profile");
            Assert.True(target.IsDeployed); Assert.False(f.Services.Operations.IsError);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeployedProfileHasGreenBorderUntilLoadoutChangesOrDeploymentIsDamaged()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        var deployed = Assert.Single(f.Shell.SidebarProfiles);
        Assert.False(deployed.IsDeployed);
        await profiles.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.True(deployed.IsDeployed); Assert.Contains("Deployed", deployed.Summary);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(), b => ReferenceEquals(b.DataContext, deployed));
            Assert.Equal(Avalonia.Media.Color.Parse("#4AB98A"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(button.BorderBrush).Color);
            window.MouseMove(button.TranslatePoint(new Point(24, 24), window)!.Value);
            window.CaptureRenderedFrame()?.Dispose();
            var presenter = Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>(), c => c.Name == "PART_ContentPresenter");
            Assert.Equal(Avalonia.Media.Color.Parse("#4AB98A"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.BorderBrush).Color);
            f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
            Assert.True(deployed.IsDeployed); Assert.False(deployed.IsActive);
            Assert.False(f.Shell.SidebarProfiles.Single(p => p.Name == "Other").IsDeployed);
            await deployed.SelectCommand.ExecuteAsync();
            await profiles.ToggleCommand.ExecuteAsync(); Assert.False(deployed.IsDeployed);
            await profiles.ToggleCommand.ExecuteAsync(); Assert.True(deployed.IsDeployed);
            File.WriteAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0"), [0]);
            await f.Services.Session.ReloadAsync(CancellationToken.None); Assert.False(deployed.IsDeployed);
            await profiles.PurgeCommand.ExecuteAsync(); Assert.False(deployed.IsDeployed);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SelectedProfileKeepsOrangeBorderOverHoverBackground()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.DataContext is SidebarProfile { IsActive: true });
            var presenter = Assert.Single(button.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>(),
                control => control.Name == "PART_ContentPresenter");
            var idleBackground = presenter.Background;
            window.MouseMove(button.TranslatePoint(new Point(24, 24), window)!.Value);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(button.IsPointerOver);
            Assert.Equal(Avalonia.Media.Color.Parse("#F27A22"), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(presenter.BorderBrush).Color);
            Assert.Equal(new Thickness(2), presenter.BorderThickness);
            Assert.NotEqual(idleBackground, presenter.Background);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SwitchingProfilesPreservesSidebarControlsAndPageLayout()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.InputText = "Alternate"; await f.Shell.AddProfileCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var originalButtons = window.GetVisualDescendants().OfType<Button>()
                .Where(button => button.DataContext is SidebarProfile).ToArray();
            var originalItems = f.Shell.SidebarProfiles.ToArray();
            var page = Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>());
            var host = window.FindControl<ContentControl>("PageHost")!;
            var initialBounds = host.Bounds;
            var observedSelection = false;
            f.Services.Operations.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(OperationState.IsBusy) || !f.Services.Operations.IsBusy) return;
                observedSelection = true;
                Assert.False(f.Services.Operations.IsProgressVisible);
                window.CaptureRenderedFrame()?.Dispose();
                Assert.Equal(initialBounds, host.Bounds);
            };
            await originalItems.Single(item => item.Name == "Default").SelectCommand.ExecuteAsync();
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(observedSelection);
            Assert.Same(page, Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>()));
            Assert.Equal(initialBounds, host.Bounds);
            foreach (var button in originalButtons)
            {
                Assert.Contains(button, window.GetVisualDescendants().OfType<Button>());
                Assert.Contains(Assert.IsType<SidebarProfile>(button.DataContext), f.Shell.SidebarProfiles);
            }
            var notifications = 0;
            f.Shell.CurrentPage.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ProfilesViewModel.Entries)) notifications++; };
            await originalItems.Single(item => item.Name == "Alternate").SelectCommand.ExecuteAsync();
            Assert.Equal(1, notifications);
            Assert.Same(originalItems[0], f.Shell.SidebarProfiles[0]);
            Assert.Same(originalItems[1], f.Shell.SidebarProfiles[1]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SidebarSelectsPersistsAndOpensProfilesIncludingActiveProfile()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape");
        await Page<ModsViewModel>(f, PageKind.Mods).ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync();
        f.Dialogs.InputText = "Alternate";
        await f.Shell.AddProfileCommand.ExecuteAsync();
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
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.Equal("Default", profiles.SelectedProfile!.Name);
            Assert.Equal("Default", f.Shell.ProfileLabel);
            Assert.Equal("1 selected for deployment", f.Shell.SelectionSummary);
            Assert.Equal("Default", Assert.Single(f.Shell.SidebarProfiles, item => item.IsActive).Name);
            Assert.Empty(f.Services.Session.Inspection!.Ledger.Files);
            var reopened = new AppServices(f.Data, f.Dialogs, () => []);
            await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal("Default", reopened.Session.ActiveProfile!.Name);
            // Opening the active profile must work after visiting the library too.
            f.Shell.Navigate(PageKind.Mods); Dispatcher.UIThread.RunJobs();
            var active = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                button => button.DataContext is SidebarProfile { Name: "Default" });
            await Assert.IsType<AsyncCommand>(active.Command).ExecuteAsync();
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            var plus = window.FindControl<Button>("AddProfileButton")!;
            f.Dialogs.InputText = null;
            await Assert.IsType<AsyncCommand>(plus.Command).ExecuteAsync();
            Assert.Equal(2, f.Shell.SidebarProfiles.Count);
            Assert.Equal("Default", profiles.SelectedProfile!.Name);
            f.Dialogs.InputText = "   ";
            await f.Shell.AddProfileCommand.ExecuteAsync(); Assert.Equal(2, f.Shell.SidebarProfiles.Count);
            f.Dialogs.InputText = "  My loadout  ";
            await Assert.IsType<AsyncCommand>(plus.Command).ExecuteAsync();
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.Equal("My loadout", profiles.SelectedProfile!.Name);
            Assert.Equal(3, f.Shell.SidebarProfiles.Count);
            Assert.Empty(profiles.Entries);
            f.Dialogs.InputText = "Second loadout";
            await f.Shell.AddProfileCommand.ExecuteAsync();
            Assert.Equal("Second loadout", profiles.SelectedProfile!.Name);
            var persisted = new AppServices(f.Data, f.Dialogs, () => []);
            await persisted.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal("Second loadout", persisted.Session.ActiveProfile!.Name);
            Assert.Equal(4, persisted.Session.State.Profiles.Count);
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            Assert.DoesNotContain(window.GetVisualDescendants(), control => control is Expander);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileContextMenuTargetsClickedProfileAndPersistsChanges()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
        f.Shell.Navigate(PageKind.Mods);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.DataContext is SidebarProfile { Name: "Default" });
            var menu = button.ContextMenu!; menu.Open(button); Dispatcher.UIThread.RunJobs();
            var rename = Assert.IsType<MenuItem>(menu.Items[0]);
            f.Dialogs.InputText = "  Renamed default  ";
            await Assert.IsType<AsyncCommand>(rename.Command).ExecuteAsync(); menu.Close();
            Assert.Equal("Default", f.Dialogs.InitialInputText);
            Assert.Equal("Other", f.Services.Session.ActiveProfile!.Name);
            Assert.Equal(PageKind.Mods, f.Shell.SelectedNavigation.Page);
            Assert.Contains(f.Shell.SidebarProfiles, profile => profile.Name == "Renamed default");
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            button = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                b => b.DataContext is SidebarProfile { Name: "Renamed default" });
            menu = button.ContextMenu!; menu.Open(button); Dispatcher.UIThread.RunJobs();
            var delete = Assert.IsType<MenuItem>(menu.Items[1]);
            f.Dialogs.Confirm = false; await Assert.IsType<AsyncCommand>(delete.Command).ExecuteAsync();
            Assert.Equal(2, f.Shell.SidebarProfiles.Count);
            f.Dialogs.Confirm = true; await Assert.IsType<AsyncCommand>(delete.Command).ExecuteAsync(); menu.Close();
            Assert.Equal("Other", Assert.Single(f.Shell.SidebarProfiles).Name);
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.Equal("Other", Assert.Single(persisted.Profiles).Name);
            Assert.Equal(f.Services.Session.ActiveProfile.Id, persisted.ActiveProfileId);
            await Assert.Single(f.Shell.SidebarProfiles).SelectCommand.ExecuteAsync();
            await Assert.Single(f.Shell.SidebarProfiles).DeleteCommand.ExecuteAsync();
            Assert.Empty(f.Shell.SidebarProfiles); Assert.Equal(PageKind.Mods, f.Shell.SelectedNavigation.Page);
            Assert.Empty((await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync()).Profiles);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileNameDialogValidatesInputAndReturnsNameOrCancellation()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var owner = new MainWindow { DataContext = f.Shell }; owner.Show();
        try
        {
            var dialogs = new DialogService(() => owner);
            var result = dialogs.RequestTextAsync("Create profile", "Name your profile", "Create");
            using (var frame = owner.CaptureRenderedFrame())
            {
                if (frame is not null && Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } output)
                {
                    Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "ProfileDialog.png"));
                }
            }
            var popup = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            Assert.Empty(owner.OwnedWindows);
            Assert.False(owner.FindControl<Grid>("ShellContent")!.IsEnabled);
            var input = popup.FindControl<TextBox>("NameInput")!;
            Assert.True(input.IsFocused);
            var accept = popup.FindControl<Button>("AcceptButton")!;
            Assert.False(accept.IsEnabled);
            input.Text = "   "; Dispatcher.UIThread.RunJobs(); Assert.False(accept.IsEnabled);
            input.Text = "  Squad alpha  "; Dispatcher.UIThread.RunJobs(); Assert.True(accept.IsEnabled);
            accept.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Squad alpha", await result);
            Assert.True(owner.FindControl<Grid>("ShellContent")!.IsEnabled);
            result = dialogs.RequestTextAsync("Create profile", "Name your profile", "Create");
            owner.CaptureRenderedFrame()?.Dispose();
            popup = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            var cancel = Assert.Single(popup.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "Cancel"));
            cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.Null(await result);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ModalOverlaySupportsKeyboardFocusAndConfirmationWithoutExtraWindows()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var owner = new MainWindow { DataContext = f.Shell }; owner.Show();
        try
        {
            owner.CaptureRenderedFrame()?.Dispose();
            var origin = owner.FindControl<Button>("AddProfileButton")!; origin.Focus();
            var dialogs = new DialogService(() => owner);
            var answer = dialogs.ConfirmAsync("Delete profile", "Delete this profile?", "Delete");
            owner.CaptureRenderedFrame()?.Dispose(); Dispatcher.UIThread.RunJobs();
            var modal = Assert.Single(owner.GetVisualDescendants().OfType<ConfirmationDialog>());
            var overlay = owner.FindControl<DialogHost>("DialogOverlay")!;
            Assert.Empty(owner.OwnedWindows); Assert.True(overlay.IsOpen);
            Assert.False(owner.FindControl<Grid>("ShellContent")!.IsEnabled);
            modal.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Assert.False(await answer); Dispatcher.UIThread.RunJobs();
            Assert.False(overlay.IsOpen); Assert.True(origin.IsFocused);
            answer = dialogs.ConfirmAsync("Delete profile", "Delete this profile?", "Delete");
            owner.CaptureRenderedFrame()?.Dispose();
            modal = Assert.Single(owner.GetVisualDescendants().OfType<ConfirmationDialog>());
            modal.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.True(await answer); Assert.False(overlay.IsOpen);
            var text = dialogs.RequestTextAsync("Create profile", "Name your profile", "Create");
            owner.CaptureRenderedFrame()?.Dispose();
            var naming = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            naming.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.False(text.IsCompleted);
            naming.FindControl<TextBox>("NameInput")!.Text = "Named profile";
            naming.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Enter });
            Assert.Equal("Named profile", await text);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingWindowDismissesDialogAndReleasesWaitingOperation()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var owner = new MainWindow { DataContext = f.Shell }; owner.Show();
        var dialogs = new DialogService(() => owner);
        var operation = f.Services.Operations.RunAsync("Waiting for confirmation", async _ =>
            await dialogs.ConfirmAsync("Confirm", "Continue?", "Continue"));
        Dispatcher.UIThread.RunJobs();
        Assert.True(owner.FindControl<DialogHost>("DialogOverlay")!.IsOpen);
        owner.Close(); await operation; await f.Services.Operations.WhenIdle;
        Dispatcher.UIThread.RunJobs();
        Assert.False(owner.IsVisible); Assert.False(f.Services.Operations.IsBusy);
        Assert.False(owner.FindControl<DialogHost>("DialogOverlay")!.IsOpen);
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
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        var foreign = Path.Combine(f.Game, Fixture.Archive + ".patch_8.stream");
        f.Dialogs.Confirm = false; await profiles.DeployCommand.ExecuteAsync();
        Assert.Null(f.Services.Session.Inspection!.Ledger.SelectionId);
        f.Dialogs.Confirm = true;
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
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == option);
            var details = window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ModDetailsButton");
            details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var popup = Assert.Single(window.GetVisualDescendants().OfType<ModSettingsDialog>());
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Empty(window.OwnedWindows);
            Assert.False(window.FindControl<Grid>("ShellContent")!.IsEnabled);
            var choice = Assert.Single(popup.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == option);
            choice.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, option.ChoiceIndex);
            await profiles.ApplyOptionsCommand.ExecuteAsync();
            var state = f.Services.Session.State; var mod = Assert.Single(state.Mods); var entry = Assert.Single(profiles.SelectedProfile!.Entries);
            Assert.Equal(new[] { "common", "red" }, PatchSelection.Select(mod, entry).Select(p => p.Folder));
            var reopened = new AppServices(f.Data, f.Dialogs, () => []); await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal(1, Assert.Single(reopened.Session.ActiveProfile!.Entries[0].Options).ChoiceIndex);
            popup.Cancel(); Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GlobalRepatchPolicyPromptsOnlyForChangedPatchesAndPersists()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var originalSource = f.UnitSource("Outdated armor");
        await f.Services.Session.ImportAsync(originalSource, CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        var mod = Assert.Single(f.Services.Session.State.Mods);
        var original = File.ReadAllBytes(Path.Combine(originalSource, Fixture.Archive + ".patch_7"));
        Assert.Equal(RepatchMode.Ask, f.Services.Session.Settings.Repatch);
        f.Dialogs.Confirm = false;
        await f.Services.Session.DeployAsync(profiles.SelectedProfile!.Id, f.Dialogs, CancellationToken.None);
        Assert.Contains(f.Dialogs.Confirmations, c => c.Title == "Repatch required" && c.Message.Contains(mod.Name));
        Assert.False(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
        f.Dialogs.Confirm = true;
        await profiles.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.NotEqual(original, File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(f.Data, "library", mod.Id.ToString("N"), Fixture.Archive + ".patch_7")));
        Assert.False(Directory.Exists(Path.Combine(f.Data, "patched")));
        Assert.False(File.Exists(Path.Combine(f.Data, "patches.json")));
        var settings = Page<SettingsViewModel>(f, PageKind.Settings);
        settings.RepatchChoice = (int)RepatchMode.Automatic; await settings.SaveRepatchCommand.ExecuteAsync();
        f.Dialogs.Confirmations.Clear(); await profiles.DeployCommand.ExecuteAsync();
        Assert.DoesNotContain(f.Dialogs.Confirmations, c => c.Title == "Repatch required");
        Assert.False(f.Services.Operations.IsError);
        var saved = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
        Assert.Equal(RepatchMode.Automatic, saved.Repatch);
        settings.RepatchChoice = (int)RepatchMode.Never; await settings.SaveRepatchCommand.ExecuteAsync();
        await profiles.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
    }

    [AvaloniaFact]
    public async Task CompatibleModsDoNotPromptAndMissingUnitsBlockAutomaticDeployment()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.UnitSource("Current armor", compatible: true), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        await profiles.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        Assert.DoesNotContain(f.Dialogs.Confirmations, c => c.Title == "Repatch required");
        var previous = File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0"));
        await f.Services.Session.ImportAsync(f.UnitSource("Missing armor", missing: true), CancellationToken.None);
        await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Automatic, CancellationToken.None);
        await profiles.DeployCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Contains("missing units", f.Services.Operations.Message);
        Assert.Equal(previous, File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
    }

    [AvaloniaFact]
    public async Task LibraryExportsRepatchedZipWithoutDeploymentAndHandlesSaveCancellation()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var source = f.UnitSource("Export armor");
        File.WriteAllText(Path.Combine(source, "readme.txt"), "Keep the metadata");
        await f.Services.Session.ImportAsync(source, CancellationToken.None);
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        await mods.ExportRepatchedCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        f.Dialogs.SavePath = Path.Combine(f.Root, "export.zip");
        await mods.ExportRepatchedCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
        using var zip = System.IO.Compression.ZipFile.OpenRead(f.Dialogs.SavePath);
        using var stream = zip.GetEntry(Fixture.Archive + ".patch_7")!.Open();
        using var bytes = new MemoryStream(); await stream.CopyToAsync(bytes);
        Assert.NotEqual(File.ReadAllBytes(Path.Combine(source, Fixture.Archive + ".patch_7")), bytes.ToArray());
        Assert.NotNull(zip.GetEntry("readme.txt")); Assert.NotNull(zip.GetEntry(Fixture.Archive + ".patch_7.stream"));
        Assert.False(File.Exists(Path.Combine(f.Data, "deployment.lock")));
        Assert.Single(Directory.EnumerateFiles(f.Game));
        Assert.False(Directory.Exists(Path.Combine(f.Data, "patched")));
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
        Assert.Equal("Default", settings.ActiveProfileName);
        var previousPriority = f.Services.Session.ActiveProfile!.Priority;
        await settings.PriorityCommand.ExecuteAsync();
        Assert.NotEqual(previousPriority, f.Services.Session.ActiveProfile!.Priority);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.NotEqual(previousPriority, f.Services.Session.ActiveProfile!.Priority);
        await settings.PriorityCommand.ExecuteAsync();
        Assert.Equal(previousPriority, f.Services.Session.ActiveProfile!.Priority);
        settings.GamePath = "unsaved edit"; await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Equal("unsaved edit", settings.GamePath);
    }

    [AvaloniaFact]
    public async Task ModifiedDeploymentRequiresPurgeAndErrorAppearsInShell()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.ZipPath = f.Zip("Cape"); await Page<ModsViewModel>(f, PageKind.Mods).ImportZipCommand.ExecuteAsync();
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync(); await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
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
        await profiles.AddCommand.ExecuteAsync(); await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        await profiles.DeployCommand.ExecuteAsync();
        f.Dialogs.Confirmations.Clear();
        await profiles.RunCommand.ExecuteAsync();
        Assert.Equal(1, f.Launches); Assert.Empty(f.Dialogs.Confirmations);
        await profiles.ToggleCommand.ExecuteAsync();
        f.Dialogs.Confirm = false; await profiles.RunCommand.ExecuteAsync();
        Assert.Equal(1, f.Launches);
        Assert.Contains("loadout is not deployed", f.Dialogs.Confirmations[^1].Message);
        f.Dialogs.Confirm = true; await profiles.RunCommand.ExecuteAsync(); Assert.Equal(2, f.Launches);
        f.Dialogs.InputText = "B";
        await f.Shell.AddProfileCommand.ExecuteAsync();
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
