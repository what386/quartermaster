using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Settings;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ManagementFeatureTests
{
    [AvaloniaFact]
    public async Task ConflictInspectorShowsContributorsAndPreviewsPriorityWithoutChangingTheProfile()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var a = await f.Services.Library.ImportAsync(f.Source("Armor A"));
        var b = await f.Services.Library.ImportAsync(f.Source("Armor B"));
        var disabled = await f.Services.Library.ImportAsync(f.Source("Disabled armor"));
        var other = await f.Services.Library.ImportAsync(f.Source("Different resource", 2));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Conflicts"), a), b), disabled), other);
        profile = ProfileEditor.SetEnabled(profile, disabled.Id, false);
        await f.Services.Session.SaveProfileAsync(profile, true, default);
        using var inspector = new ConflictInspectorViewModel(f.Services, profile.Id);
        var conflict = Assert.Single(inspector.Resources);
        Assert.Equal(b.Id, conflict.Conflict.WinningSourceId);
        Assert.Equal(2, inspector.Contributors.Count);
        Assert.Equal("Armor B", Assert.Single(inspector.Contributors, mod => mod.IsWinner).Name);
        Assert.All(inspector.Contributors, mod => Assert.Contains(Fixture.Archive + ".patch_7", mod.Files));
        Assert.DoesNotContain(inspector.Contributors, mod => mod.Name == disabled.Name || mod.Name == other.Name);
        inspector.PriorityChoice = 1;
        Assert.True(inspector.IsPreview);
        Assert.Equal(a.Id, Assert.Single(inspector.Resources).Conflict.WinningSourceId);
        Assert.Equal(PriorityDirection.LastWins, f.Services.Session.ActiveProfile!.Priority);
        Assert.Equal(PriorityDirection.LastWins, (await f.Services.Library.LoadAsync()).Profiles.Single(p => p.Id == profile.Id).Priority);
        inspector.Search = "Armor A"; Assert.Single(inspector.VisibleResources);
        inspector.Search = "no match"; Assert.Empty(inspector.VisibleResources); Assert.Empty(inspector.Contributors);
        inspector.Search = "";
        await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(profile, b.Id, false), true, default);
        Assert.Empty(inspector.Resources);
        Assert.Single(inspector.SharedArchives);
    }

    [AvaloniaFact]
    public async Task ProfileButtonOpensAVisualConflictInspectorWithWinningCards()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var a = await f.Services.Library.ImportAsync(f.Source("A"));
        var b = await f.Services.Library.ImportAsync(f.Source("B"));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Conflict view"), a), b);
        await f.Services.Session.SaveProfileAsync(profile, true, default); f.Shell.Navigate(PageKind.Profiles);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose(); Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>());
            view.FindControl<Button>("InspectConflictsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<ConflictInspectorDialog>());
            Assert.Single(dialog.GetVisualDescendants().OfType<Border>(), border => border.Classes.Contains("winner"));
            var inspector = Assert.IsType<ConflictInspectorViewModel>(dialog.DataContext);
            dialog.FindControl<ComboBox>("ConflictPriority")!.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("A", Assert.Single(inspector.Contributors, mod => mod.IsWinner).Name);
            dialog.Cancel(); Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DescriptionDraftSurvivesUnrelatedRefreshAndSavesToTheSelectedProfile()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Profiles);
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        model.DescriptionDraft = "Armor and HUD\nUse this for squad nights.";
        await f.Services.Session.ReloadAsync(default);
        Assert.Equal("Armor and HUD\nUse this for squad nights.", model.DescriptionDraft);
        await model.SaveDescriptionCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError);
        Assert.Equal(model.DescriptionDraft, f.Services.Session.ActiveProfile!.Description);
        Assert.True(model.HasDescription);
        var saved = (await f.Services.Library.LoadAsync()).Profiles.Single(p => p.Id == model.SelectedProfile!.Id);
        Assert.Equal(model.DescriptionDraft, saved.Description);
        model.DescriptionDraft = "";
        await model.SaveDescriptionCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError);
        Assert.False(model.HasDescription);
        Assert.Equal("", (await f.Services.Library.LoadAsync()).Profiles.Single(p => p.Id == model.SelectedProfile!.Id).Description);
        var another = ProfileEditor.Create("Another");
        await f.Services.Session.SaveProfileAsync(another, true, default);
        Assert.Equal("", model.DescriptionDraft);
        model.DescriptionDraft = new string('a', ProfileEditor.MaxDescriptionLength + 1);
        Assert.False(model.SaveDescriptionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StorageCleanupRequiresSelectionAndConfirmationAndRefreshesTheLibrary()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var old = await f.Services.Library.ImportAsync(f.Source("Previous armor", 1));
        var current = await f.Services.Library.ImportAsync(f.Source("Current armor", 2));
        await f.Services.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        await f.Services.Session.ReloadAsync(default); f.Shell.Navigate(PageKind.Settings);
        var model = Assert.IsType<SettingsViewModel>(f.Shell.CurrentPage);
        await model.ScanStorageCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError); Assert.True(model.HasStorageReport);
        Assert.Equal(2, model.StorageMods.Count);
        Assert.False(model.CleanStorageCommand.CanExecute(null));
        var candidate = Assert.Single(model.StorageCandidates); candidate.IsSelected = true;
        Assert.True(model.CleanStorageCommand.CanExecute(null));
        f.Dialogs.Confirm = false; await model.CleanStorageCommand.ExecuteAsync();
        Assert.Equal(2, f.Services.Session.State.Mods.Count);
        Assert.Contains(old.Name, Assert.Single(f.Dialogs.Confirmations).Message);
        f.Dialogs.Confirm = true; await model.CleanStorageCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError);
        Assert.Equal(current.Id, Assert.Single(f.Services.Session.State.Mods).Id);
        Assert.Empty(model.StorageCandidates); Assert.Single(model.StorageMods);
        Assert.Contains("1 item removed", model.StorageCleanupResult);
        model.Search = "cleanup"; Assert.True(model.ShowStorage);
    }

    [AvaloniaFact]
    public async Task StorageButtonOpensStorageManagement()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); f.Shell.Navigate(PageKind.Settings);
        var old = await f.Services.Library.ImportAsync(f.Source("Previous armor", 1));
        var current = await f.Services.Library.ImportAsync(f.Source("Current armor", 2));
        await f.Services.Library.ReplaceInProfilesAsync(old.Id, current.Id);
        await f.Services.Session.ReloadAsync(default);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose(); Dispatcher.UIThread.RunJobs();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            var model = Assert.IsType<SettingsViewModel>(view.DataContext);
            await model.RefreshStorageOverviewAsync(); Dispatcher.UIThread.RunJobs();
            Assert.False(f.Services.Operations.IsBusy);
            Assert.True(model.HasStorageReport);
            var card = view.FindControl<Border>("StorageSettingsSection")!;
            var manage = view.FindControl<Button>("ManageStorageButton")!;
            Assert.Contains(card, manage.GetVisualAncestors());
            Assert.DoesNotContain(view.FindControl<Border>("AppInformationSection")!, manage.GetVisualAncestors());
            Assert.Equal(model.StorageTotal, view.FindControl<TextBlock>("StorageTotalValue")!.Text);
            Assert.Equal(model.StorageReclaimable, view.FindControl<TextBlock>("StorageReclaimableValue")!.Text);
            Assert.Equal("227 B", model.StorageReclaimable);
            view.FindControl<Button>("ManageStorageButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await f.Services.Operations.WhenIdle; Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<StorageDialog>());
            Assert.True(Assert.IsType<SettingsViewModel>(dialog.DataContext).HasStorageReport);
            dialog.Cancel(); Dispatcher.UIThread.RunJobs();
        }
        finally { window.Close(); }
    }
}
