using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Search;
using Quartermaster.Gui.Downloads;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;
using Quartermaster.Core.Deployment;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class GuiTests
{
    [AvaloniaFact]
    public async Task TestServicesRecordExternalLaunchesIncludingReopenedServices()
    {
        using var f = new Fixture();
        var page = new Uri("https://www.nexusmods.com/helldivers2/mods/123");
        f.Services.OpenBrowser(page); f.Services.LaunchGame();
        await using var reopened = f.ReopenServices();
        reopened.OpenBrowser(page); reopened.LaunchGame();
        Assert.Equal(new[] { page, page }, f.BrowserRequests);
        Assert.Equal(2, f.Launches);
    }

    private static T Page<T>(Fixture fixture, PageKind page) where T : ViewModelBase
    { fixture.Shell.Navigate(page); return Assert.IsType<T>(fixture.Shell.CurrentPage); }

    [AvaloniaFact]
    public async Task DownloadsTabExposesBrowserAttachAndCancelWithoutBlockingTheApp()
    {
        Uri? opened = null;
        using var f = new Fixture(openBrowser: uri => opened = uri); await f.Shell.InitializeAsync();
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        var folder = Path.Combine(f.Root, "downloads"); Directory.CreateDirectory(folder);
        await f.Services.Providers.SetDirectoriesAsync([folder]);
        var file = new Quartermaster.Providers.Providers.ProviderFile("nexusmods", "123", "456", "Test mod", "mod.zip", "1",
            new("https://www.nexusmods.com/helldivers2/mods/123?tab=files&file_id=456"));
        var job = await f.Services.Providers.QueueAsync(file); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Services.Operations.IsBusy); var row = Assert.Single(mods.Downloads.Jobs);
        var window = new MainWindow { DataContext = f.Shell, Width = 1100, Height = 1000 }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var nav = f.Shell.UtilityNavigationItems.Single(item => item.Page == PageKind.Downloads);
            Assert.EndsWith("downloads.svg", nav.IconSource); nav.OpenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<DownloadsView>());
            Assert.False(window.FindControl<Border>("DownloadProgressOverlay")!.IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Attach ZIP") && b.IsVisible);
            await row.OpenCommand.ExecuteAsync(); Assert.Equal(file.DownloadPage, opened);
            Assert.True(row.CancelCommand.CanExecute(null)); Assert.False(row.RetryCommand.CanExecute(null));
            await row.CancelCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Empty(mods.Downloads.Jobs); Assert.Empty(f.Services.Providers.State.Jobs);
            Assert.False(window.FindControl<Border>("DownloadProgressOverlay")!.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("empty")]
    [InlineData("profile")]
    [InlineData("plus")]
    [InlineData("library")]
    public async Task ProfileArchiveDropsAnywhereInSidebarCreateAndSelectNewProfile(string target)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        var original = f.Services.Session.ActiveProfile!;
        var path = Path.Combine(f.Root, "loadout.zip");
        await f.Services.Session.ExportProfileAsync(original.Id, path, CancellationToken.None);
        f.Shell.Navigate(PageKind.Settings);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var sidebar = window.FindControl<Border>("Sidebar")!;
            Control control = target switch
            {
                "profile" => window.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is SidebarProfile),
                "plus" => window.FindControl<Button>("AddProfileButton")!,
                "library" => window.GetVisualDescendants().OfType<Button>().Single(button => button.DataContext is NavigationItem { Page: PageKind.Mods }),
                _ => sidebar
            };
            var position = control.TranslatePoint(new Point(target == "empty" ? control.Bounds.Width - 4 : control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
            var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
            var data = new DataTransfer(); data.Add(DataTransferItem.CreateFile(file!));
            var over = new DragEventArgs(DragDrop.DragOverEvent, data, window, position, KeyModifiers.None);
            window.FindControl<Grid>("ShellContent")!.RaiseEvent(over);
            Assert.Equal(DragDropEffects.Copy, over.DragEffects);
            window.DragDrop(position, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
            window.DragDrop(position, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
            await f.Services.Operations.WhenIdle; await f.Services.Operations.WhenIdle;
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            Assert.Equal(2, f.Shell.SidebarProfiles.Count); Assert.Single(f.Services.Session.State.Mods);
            Assert.NotEqual(original.Id, f.Services.Session.ActiveProfile!.Id);
            Assert.Equal(original.Entries.Select(entry => (entry.ModId, entry.Enabled)), f.Services.Session.ActiveProfile.Entries.Select(entry => (entry.ModId, entry.Enabled)));
            Assert.Equal(original.Entries.Select(entry => (entry.ModId, entry.Enabled)), f.Services.Session.State.Profiles.Single(profile => profile.Id == original.Id).Entries.Select(entry => (entry.ModId, entry.Enabled)));
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.DoesNotContain(Directory.GetFiles(f.Game), filePath => filePath.Contains(".patch_"));
            if (target == "empty")
            {
                var folder = await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(f.Source("Folder")));
                var folderData = new DataTransfer(); folderData.Add(DataTransferItem.CreateFile(folder!));
                over = new DragEventArgs(DragDrop.DragOverEvent, folderData, window, position, KeyModifiers.None);
                window.FindControl<Grid>("ShellContent")!.RaiseEvent(over);
                Assert.Equal(DragDropEffects.None, over.DragEffects);
            }
        }
        finally { await f.Services.Operations.WhenIdle; window.Close(); }
    }

    [AvaloniaFact]
    public async Task CreateProfileDialogOffersFileChoiceWithoutANameAndRenameDoesNot()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var owner = new MainWindow { DataContext = f.Shell }; owner.Show();
        try
        {
            var dialogs = new DialogService(() => owner);
            var request = dialogs.RequestProfileCreationAsync(); owner.CaptureRenderedFrame()?.Dispose();
            var dialog = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            Assert.False(dialog.FindControl<Button>("AcceptButton")!.IsEnabled);
            var file = dialog.FindControl<Button>("ChooseFileButton")!;
            Assert.True(file.IsEffectivelyVisible); Assert.True(file.IsEffectivelyEnabled);
            file.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.True((await request)!.FromFile); Assert.False(owner.FindControl<DialogHost>("DialogOverlay")!.IsOpen);
            request = dialogs.RequestProfileCreationAsync(); owner.CaptureRenderedFrame()?.Dispose();
            dialog = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            dialog.FindControl<TextBox>("NameInput")!.Text = "  Named  "; dialog.TryAccept();
            Assert.Equal("Named", (await request)!.Name);
            var rename = dialogs.RequestTextAsync("Rename profile", "Profile name", "Rename", "Named"); owner.CaptureRenderedFrame()?.Dispose();
            dialog = Assert.Single(owner.GetVisualDescendants().OfType<TextInputDialog>());
            Assert.False(dialog.FindControl<Button>("ChooseFileButton")!.IsVisible); dialog.Cancel(); Assert.Null(await rename);
            Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    }

    [AvaloniaFact]
    public async Task CreateProfileFromFileImportsLoadoutAndCancellingPickerLeavesProfilesUnchanged()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var original = f.Services.Session.ActiveProfile!;
        var path = Path.Combine(f.Root, "loadout.zip"); await f.Services.Session.ExportProfileAsync(original.Id, path, CancellationToken.None);
        f.Dialogs.CreateProfileFromFile = true;
        await f.Shell.AddProfileCommand.ExecuteAsync(); Assert.Single(f.Shell.SidebarProfiles);
        f.Dialogs.ZipPath = path; await f.Shell.AddProfileCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(2, f.Shell.SidebarProfiles.Count); Assert.NotEqual(original.Id, f.Services.Session.ActiveProfile!.Id);
        Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
    }

    [AvaloniaFact]
    public async Task SidebarProfileArchivesExportClickedProfileAndImportSelectsNewProfileWithoutDuplicatingMods()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.OptionsSource(), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Disabled cape"), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        var original = profiles.SelectedProfile!;
        var variants = f.Services.Session.State.Mods.Single(mod => mod.Name == "Armor variants");
        var cape = f.Services.Session.State.Mods.Single(mod => mod.Name == "Disabled cape");
        original = ProfileEditor.SetOptions(original, variants, [new(variants.Options[0].Id, true, 1)]);
        original = ProfileEditor.SetEnabled(original, cape.Id, false);
        original = ProfileEditor.AddGroup(original, "Equipment", [variants.Id]);
        await f.Services.Session.SaveProfileAsync(original, true, CancellationToken.None);
        f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
        var otherId = f.Services.Session.ActiveProfile!.Id;
        var destination = Path.Combine(f.Root, "loadout.zip"); f.Dialogs.SavePath = destination;
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = window.GetVisualDescendants().OfType<Button>().Single(item => item.DataContext is SidebarProfile profile && profile.Profile.Id == original.Id);
            var menu = button.ContextMenu!; menu.Open(button); Dispatcher.UIThread.RunJobs();
            var export = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Export profile ZIP"));
            await Assert.IsType<AsyncCommand>(export.Command).ExecuteAsync(); menu.Close();
            Assert.True(File.Exists(destination)); Assert.Equal("Default-profile.zip", f.Dialogs.SuggestedSaveName);
            Assert.Equal(otherId, f.Services.Session.ActiveProfile!.Id);
            var plus = window.FindControl<Button>("AddProfileButton")!;
            menu = plus.ContextMenu!; menu.Open(plus); Dispatcher.UIThread.RunJobs();
            var import = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Import profile ZIP"));
            f.Dialogs.ZipPath = destination;
            await Assert.IsType<AsyncCommand>(import.Command).ExecuteAsync(); menu.Close();
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            Assert.Equal(2, f.Services.Session.State.Mods.Count); Assert.Equal(3, f.Shell.SidebarProfiles.Count);
            var imported = f.Services.Session.ActiveProfile!;
            Assert.NotEqual(original.Id, imported.Id); Assert.Equal("Default", imported.Name);
            Assert.Equal(original.Entries.Select(entry => (entry.ModId, entry.Enabled)), imported.Entries.Select(entry => (entry.ModId, entry.Enabled)));
            Assert.Equal(1, imported.Entries.Single(entry => entry.ModId == variants.Id).Options[0].ChoiceIndex);
            Assert.Equal("Equipment", Assert.Single(imported.Groups).Name);
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.Equal(imported.Id, profiles.SelectedProfile!.Id);
            Assert.DoesNotContain(Directory.GetFiles(f.Game), file => file.Contains(".patch_"));
            f.Dialogs.ZipPath = null; await f.Shell.ImportProfileCommand.ExecuteAsync();
            Assert.Equal(3, f.Shell.SidebarProfiles.Count);
            f.Dialogs.ZipPath = f.Zip("Not a profile"); await f.Shell.ImportProfileCommand.ExecuteAsync();
            Assert.True(f.Services.Operations.IsError); Assert.Equal(3, f.Shell.SidebarProfiles.Count);
            Assert.Equal(2, f.Services.Session.State.Mods.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task LibraryMultiSelectionContextActionsPreserveSelectionAndSkipProfileDuplicates()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" })
            await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var armor = f.Services.Session.State.Mods.Single(mod => mod.Name == "Armor");
        var target = ProfileEditor.SetEnabled(ProfileEditor.Add(ProfileEditor.Create("Target"), armor), armor.Id, false);
        await f.Services.Session.SaveProfileAsync(target, false, CancellationToken.None);
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ModsView>());
            var list = view.FindControl<ListBox>("ModsList")!;
            ListBoxItem Row(string name) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ModListItem mod && mod.Name == name);
            void Click(string name, MouseButton button = MouseButton.Left, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var row = Row(name); var point = row.TranslatePoint(new Point(240, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, button, modifiers); window.MouseUp(point, button, modifiers);
                Dispatcher.UIThread.RunJobs();
            }
            Click("Armor"); Click("Helmet", modifiers: RawInputModifiers.Control);
            Assert.Equal(2, list.SelectedItems!.Count); Assert.Equal(2, mods.SelectedMods.Count);
            Click("Helmet", MouseButton.Right);
            Assert.Equal(2, list.SelectedItems.Count);
            var menu = list.ContextMenu!; if (!menu.IsOpen) menu.Open(list);
            Assert.Equal(new[] { "Add to", "Remove from library", "Export repatched ZIP", "Mod details" },
                menu.Items.OfType<MenuItem>().Select(item => item.Header));
            Assert.IsType<Separator>(menu.Items[2]);
            Assert.False(Assert.IsType<MenuItem>(menu.Items[3]).Command!.CanExecute(null));
            Assert.False(Assert.IsType<MenuItem>(menu.Items[4]).IsEnabled);
            var addTo = Assert.IsType<MenuItem>(menu.Items[0]);
            var addTarget = addTo.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Target"));
            await Assert.IsType<AsyncCommand>(addTarget.Command).ExecuteAsync(); menu.Close();
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(2, mods.SelectedMods.Count); Assert.Equal(2, list.SelectedItems.Count);
            var entries = f.Services.Session.State.Profiles.Single(profile => profile.Id == target.Id).Entries;
            Assert.Equal(2, entries.Count); Assert.Equal(armor.Id, entries[0].ModId); Assert.False(entries[0].Enabled);
            menu.Open(list); Dispatcher.UIThread.RunJobs();
            addTarget = addTo.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Target"));
            Assert.False(addTarget.Command!.CanExecute(null)); menu.Close();
            mods.Search = "hel"; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal("Helmet", Assert.Single(mods.SelectedMods).Name);
            mods.Search = ""; window.CaptureRenderedFrame()?.Dispose();
            Click("Armor"); Click("Helmet", modifiers: RawInputModifiers.Shift);
            Assert.Equal(3, mods.SelectedMods.Count);
            Click("Cape", MouseButton.Right); Assert.Equal(3, mods.SelectedMods.Count);
            menu.Close(); f.Dialogs.Confirmations.Clear(); f.Dialogs.Confirm = false;
            await mods.RemoveCommand.ExecuteAsync(); Assert.Equal(3, mods.Mods.Count);
            Assert.Contains("Armor", Assert.Single(f.Dialogs.Confirmations).Message);
            Assert.Contains("Helmet", f.Dialogs.Confirmations[0].Message);
            f.Dialogs.Confirm = true; await mods.RemoveCommand.ExecuteAsync();
            Assert.Empty(mods.Mods); Assert.Empty(mods.SelectedMods);
            Assert.All(f.Services.Session.State.Profiles, profile => Assert.Empty(profile.Entries));
            Assert.Empty(Directory.GetDirectories(Path.Combine(f.Data, "library")));
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DraggingSelectionMovesNonAdjacentModsTogetherAndKeepsSelection()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet", "Weapon" }) await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        for (var i = 0; i < 4; i++) await profiles.AddCommand.ExecuteAsync();
        f.Dialogs.InputText = "Equipment"; await profiles.AddGroupCommand.ExecuteAsync();
        var groupId = profiles.Groups.Single().Id;
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = window.GetVisualDescendants().OfType<ProfilesView>().Single();
            var list = view.FindControl<ListBox>("ProfileModsList")!;
            ListBoxItem Row(string name) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileModItem mod && mod.Name == name);
            Point At(ListBoxItem row, bool after = false) => row.TranslatePoint(new Point(240, after ? row.Bounds.Height - 3 : row.Bounds.Height / 2), window)!.Value;
            void Click(string name, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var point = At(Row(name)); window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left, modifiers); window.MouseUp(point, MouseButton.Left, modifiers);
            }
            Click("Armor"); Click("Helmet", RawInputModifiers.Control);
            Assert.Equal(2, list.SelectedItems!.Count);
            var start = At(Row("Helmet")); var end = At(Row("Weapon"), after: true);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(2, list.SelectedItems.Count);
            var previews = view.FindControl<StackPanel>("DragVisuals")!.GetVisualDescendants().OfType<ModRowView>().ToArray();
            Assert.Equal(new[] { "Armor", "Helmet" }, previews.Select(row => ((ProfileModItem)row.DataContext!).Name));
            Assert.Contains("dragging", Row("Armor").Classes); Assert.Contains("dragging", Row("Helmet").Classes);
            Assert.DoesNotContain("dragging", Row("Cape").Classes);
            Assert.Equal(-Row("Armor").Bounds.Height, Row("Cape").GetBaseValue(Visual.RenderTransformProperty).Value!.Value.M32, 1);
            window.MouseUp(end, MouseButton.Left); await f.Services.Operations.WhenIdle; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(new[] { "Cape", "Weapon", "Armor", "Helmet" }, profiles.Entries.Select(mod => mod.Name));
            Assert.Equal(2, list.SelectedItems.Count);
            start = At(Row("Armor"));
            var header = list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileGroupItem);
            end = At(header);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            Assert.Contains("dropInto", header.Classes);
            Assert.Equal(2, view.FindControl<StackPanel>("DragVisuals")!.Children.Count);
            window.MouseUp(end, MouseButton.Left); await f.Services.Operations.WhenIdle; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.All(profiles.Entries.Where(mod => mod.Name is "Armor" or "Helmet"), mod => Assert.Equal(groupId, mod.Entry.GroupId));
            Assert.All(profiles.Entries.Where(mod => mod.Name is "Cape" or "Weapon"), mod => Assert.Null(mod.Entry.GroupId));
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.Equal(profiles.Entries.Select(mod => mod.Mod.Id), Assert.Single(persisted.Profiles).Entries.Select(entry => entry.ModId));
            Assert.Equal(new[] { "Cape", "Weapon", "Armor", "Helmet" }, profiles.Entries.Select(mod => mod.Name));
            start = At(Row("Helmet")); end = At(Row("Cape"));
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.MouseUp(end, MouseButton.Left);
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.Equal(new[] { "Cape", "Weapon", "Armor", "Helmet" }, profiles.Entries.Select(mod => mod.Name));
            Click("Armor"); Assert.Single(list.SelectedItems);
            Click("Helmet", RawInputModifiers.Shift); Assert.Equal(2, list.SelectedItems.Count);
            Click("Cape"); Assert.Single(list.SelectedItems);
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DragAnimationsPreviewModsAndGroupsAndGroupDropsPersistMemberOrder()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" }) await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        f.Dialogs.InputText = "Equipment";
        await profiles.CreateGroupFromModsAsync(profiles.Entries.Where(mod => mod.Name != "Helmet").Select(mod => mod.Mod.Id).ToArray());
        f.Dialogs.InputText = "Helmet group";
        await profiles.CreateGroupFromModsAsync([profiles.Entries.Single(mod => mod.Name == "Helmet").Mod.Id]);
        var firstId = profiles.Groups[0].Id; var secondId = profiles.Groups[1].Id;
        await profiles.VisibleItems.OfType<ProfileGroupItem>().Last().ToggleCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>());
            var list = view.FindControl<ListBox>("ProfileModsList")!;
            ListBoxItem Header(Guid id) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileGroupItem group && group.Id == id);
            Point At(ListBoxItem row, bool bottom = false) => row.TranslatePoint(new Point(240, bottom ? row.Bounds.Height - 3 : 3), window)!.Value;
            var source = Header(firstId); var start = At(source); var end = At(Header(secondId), bottom: true);
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            Assert.Contains("dragging", source.Classes);
            Assert.All(list.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.DataContext is ProfileModItem), row => Assert.Contains("dragging", row.Classes));
            Assert.True(view.FindControl<Border>("DragPreview")!.IsVisible);
            Assert.Contains(view.FindControl<StackPanel>("DragVisuals")!.GetVisualDescendants().OfType<ProfileGroupView>(),
                preview => preview.DataContext is ProfileGroupItem { Name: "Equipment" });
            var ghost = view.FindControl<Border>("DragPreview")!;
            Assert.Equal(source.Bounds.Width, ghost.Width);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(source.Bounds.Height + list.GetVisualDescendants().OfType<ListBoxItem>()
                .Where(row => row.DataContext is ProfileModItem).Sum(row => row.Bounds.Height), ghost.Bounds.Height, 1);
            Assert.Equal(-ghost.Bounds.Height, Header(secondId).GetBaseValue(Visual.RenderTransformProperty).Value!.Value.M32, 1);
            if (Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } dragDirectory)
            {
                Directory.CreateDirectory(dragDirectory);
                using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(dragDirectory, "DragGroup.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            var translation = Assert.IsType<Avalonia.Media.TranslateTransform>(ghost.RenderTransform);
            Assert.Equal(end.Y - start.Y + source.TranslatePoint(default, list)!.Value.Y, translation.Y, 1);
            var previousY = translation.Y;
            window.MouseMove(new Point(end.X + 40, end.Y - 8));
            Assert.Equal(0, translation.X); Assert.Equal(previousY - 8, translation.Y, 1);
            window.MouseMove(end);
            Assert.Contains("dropAfter", Header(secondId).Classes);
            Assert.Contains(source.Transitions!, transition => transition is Avalonia.Animation.TransformOperationsTransition);
            window.MouseUp(end, MouseButton.Left); await f.Services.Operations.WhenIdle; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(new[] { secondId, firstId }, profiles.Groups.Select(group => group.Id));
            Assert.Equal(new[] { "Helmet", "Armor", "Cape" }, profiles.Entries.Select(mod => mod.Name));
            Assert.True(profiles.Groups.Single(group => group.Id == firstId).IsExpanded);
            Assert.False(profiles.Groups.Single(group => group.Id == secondId).IsExpanded);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.Classes.Contains("dragging"));
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.Equal(new[] { secondId, firstId }, Assert.Single(persisted.Profiles).Groups.Select(group => group.Id));
            var modRow = list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileModItem mod && mod.Name == "Cape");
            start = At(modRow); end = At(Header(secondId));
            window.MouseMove(start); window.MouseDown(start, MouseButton.Left); window.MouseMove(end);
            Assert.Contains("dragging", modRow.Classes); Assert.Contains("dropInto", Header(secondId).Classes);
            Assert.Contains(view.FindControl<StackPanel>("DragVisuals")!.GetVisualDescendants().OfType<ModRowView>(),
                preview => preview.DataContext is ProfileModItem { Name: "Cape" });
            if (Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } modDragDirectory)
            {
                window.CaptureRenderedFrame()?.Dispose();
                using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(modDragDirectory, "DragMod.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(end, MouseButton.Left);
            Assert.Equal(firstId, profiles.Entries.Single(mod => mod.Name == "Cape").Entry.GroupId);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.Classes.Contains("dragging") || row.Classes.Contains("dropInto"));
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RightClickCreatesGroupsFromMultipleSingleOrNoSelectedMods()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" }) await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>());
            var list = view.FindControl<ListBox>("ProfileModsList")!;
            ListBoxItem Row(string name) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileModItem mod && mod.Name == name);
            void Click(string name, MouseButton button, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                var row = Row(name); var point = row.TranslatePoint(new Point(240, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point); window.MouseDown(point, button, modifiers); window.MouseUp(point, button, modifiers);
            }
            async Task Create(string name)
            {
                f.Dialogs.InputText = name;
                view.FindControl<MenuItem>("CreateGroupMenuItem")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                await f.Services.Operations.WhenIdle; window.CaptureRenderedFrame()?.Dispose();
                Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            }
            Click("Armor", MouseButton.Left); Click("Cape", MouseButton.Left, RawInputModifiers.Control);
            Assert.Equal(2, list.SelectedItems!.Count);
            Click("Helmet", MouseButton.Right); Assert.Single(list.SelectedItems); list.ContextMenu!.Close();
            Click("Armor", MouseButton.Left); Click("Cape", MouseButton.Left, RawInputModifiers.Shift);
            Assert.Equal(2, list.SelectedItems.Count);
            Click("Armor", MouseButton.Right); Assert.Equal(2, list.SelectedItems.Count);
            await Create("Equipment");
            var groupId = Assert.Single(profiles.Groups).Id;
            Assert.All(profiles.Entries.Where(mod => mod.Name != "Helmet"), mod => Assert.Equal(groupId, mod.Entry.GroupId));
            Assert.Null(profiles.Entries.Single(mod => mod.Name == "Helmet").Entry.GroupId);
            var grouped = Row("Armor").GetVisualDescendants().OfType<Border>().Single(border => border.Name == "GroupIndent");
            var ungrouped = Row("Helmet").GetVisualDescendants().OfType<Border>().Single(border => border.Name == "GroupIndent");
            Assert.True(grouped.IsVisible); Assert.Equal(24, grouped.Bounds.Width); Assert.False(ungrouped.IsVisible);
            list.SelectedItems.Clear(); window.CaptureRenderedFrame()?.Dispose();
            var guidePosition = grouped.TranslatePoint(default, list)!.Value;
            var modRow = Row("Armor").GetVisualDescendants().OfType<ModRowView>().Single();
            var modPosition = modRow.TranslatePoint(default, list)!.Value;
            var modSize = modRow.Bounds.Size;
            list.SelectedItem = Row("Armor").DataContext; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(guidePosition, grouped.TranslatePoint(default, list)!.Value);
            Assert.Equal(modPosition, modRow.TranslatePoint(default, list)!.Value); Assert.Equal(modSize, modRow.Bounds.Size);
            var selectionMarker = Row("Armor").GetVisualDescendants().OfType<Border>().Single(border => border.Name == "RowIndicator");
            Assert.Equal(new Thickness(2, 0, 0, 0), selectionMarker.BorderThickness);
            foreach (var marker in new[] { "dropBefore", "dropAfter", "dropInto" })
            {
                Row("Armor").Classes.Add(marker); window.CaptureRenderedFrame()?.Dispose();
                Assert.Equal(guidePosition, grouped.TranslatePoint(default, list)!.Value);
                Assert.Equal(modPosition, modRow.TranslatePoint(default, list)!.Value); Assert.Equal(modSize, modRow.Bounds.Size);
                Row("Armor").Classes.Remove(marker);
            }
            Click("Helmet", MouseButton.Right); Assert.Single(list.SelectedItems);
            await Create("Helmet only");
            Assert.Equal(profiles.Groups[1].Id, profiles.Entries.Single(mod => mod.Name == "Helmet").Entry.GroupId);
            var blank = list.TranslatePoint(new Point(240, list.Bounds.Height - 20), window)!.Value;
            window.MouseMove(blank); window.MouseDown(blank, MouseButton.Right); window.MouseUp(blank, MouseButton.Right);
            Assert.Empty(list.SelectedItems); await Create("Empty");
            Assert.DoesNotContain(profiles.Entries, mod => mod.Entry.GroupId == profiles.Groups[2].Id);
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.Equal(3, Assert.Single(persisted.Profiles).Groups.Count);
            f.Dialogs.InputText = null;
            await profiles.CreateGroupFromModsAsync(profiles.Entries.Select(mod => mod.Mod.Id).ToArray());
            Assert.Equal(3, profiles.Groups.Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileGroupsCanBeCreatedAssignedCollapsedSearchedRenamedAndRemoved()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" }) await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        f.Dialogs.InputText = "Equipment"; await profiles.AddGroupCommand.ExecuteAsync();
        var groupId = Assert.Single(profiles.Groups).Id;
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>());
            var list = view.FindControl<ListBox>("ProfileModsList")!;
            ListBoxItem Row(string name) => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileModItem mod && mod.Name == name);
            ListBoxItem Header() => list.GetVisualDescendants().OfType<ListBoxItem>().Single(row => row.DataContext is ProfileGroupItem group && group.Id == groupId);
            list.SelectedItem = profiles.VisibleItems.OfType<ProfileGroupItem>().Single();
            Assert.Null(profiles.SelectedMod); Assert.False(profiles.RemoveCommand.CanExecute(null));
            Assert.IsType<ProfileGroupItem>(profiles.SelectedListItem);
            var cape = Row("Cape"); var point = cape.TranslatePoint(new Point(240, cape.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Cape", profiles.SelectedMod!.Name);
            var menu = list.ContextMenu!; if (!menu.IsOpen) menu.Open(list);
            var move = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to group"));
            var equipment = move.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Equipment"));
            await Assert.IsType<AsyncCommand>(equipment.Command).ExecuteAsync(); menu.Close();
            Assert.Equal(groupId, profiles.Entries.Single(row => row.Name == "Cape").Entry.GroupId);
            window.CaptureRenderedFrame()?.Dispose();
            var helmet = Row("Helmet"); var header = Header();
            var from = helmet.TranslatePoint(new Point(240, helmet.Bounds.Height / 2), window)!.Value;
            var to = header.TranslatePoint(new Point(240, header.Bounds.Height / 2), window)!.Value;
            window.MouseMove(from); window.MouseDown(from, MouseButton.Left); window.MouseMove(to);
            Assert.Contains(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.Classes.Contains("dropInto"));
            window.MouseUp(to, MouseButton.Left); await f.Services.Operations.WhenIdle;
            Assert.Equal(new[] { "Armor", "Cape", "Helmet" }, profiles.Entries.Select(row => row.Name));
            Assert.Equal(groupId, profiles.Entries.Single(row => row.Name == "Helmet").Entry.GroupId);
            var group = profiles.VisibleItems.OfType<ProfileGroupItem>().Single();
            Assert.Equal("2 mods · 2 on", group.Summary);
            if (Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } screenshotDirectory)
            {
                window.CaptureRenderedFrame()?.Dispose(); Directory.CreateDirectory(screenshotDirectory);
                using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(screenshotDirectory, "Groups.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            window.CaptureRenderedFrame()?.Dispose();
            var groupView = Assert.Single(window.GetVisualDescendants().OfType<ProfileGroupView>());
            var groupButton = Assert.Single(groupView.GetVisualDescendants().OfType<Button>());
            Assert.True(groupButton.IsEnabled); Assert.Equal(list.Bounds.Width, groupButton.Bounds.Width, 1);
            var collapsePoint = groupButton.TranslatePoint(new Point(120, groupButton.Bounds.Height / 2), window)!.Value;
            window.MouseMove(collapsePoint); window.MouseDown(collapsePoint, MouseButton.Left); window.MouseUp(collapsePoint, MouseButton.Left);
            await f.Services.Operations.WhenIdle; window.CaptureRenderedFrame()?.Dispose();
            Assert.False(Assert.Single(profiles.Groups).IsExpanded);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.DataContext is ProfileModItem { Name: "Cape" or "Helmet" });
            Assert.Contains(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.DataContext is ProfileModItem { Name: "Armor" });
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.False(Assert.Single(Assert.Single(persisted.Profiles).Groups).IsExpanded);
            profiles.Search = "Cape"; window.CaptureRenderedFrame()?.Dispose();
            Assert.Contains(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.DataContext is ProfileModItem { Name: "Cape" });
            Assert.False(Assert.Single(profiles.Groups).IsExpanded);
            profiles.Search = "";
            group = profiles.VisibleItems.OfType<ProfileGroupItem>().Single();
            f.Dialogs.InputText = "Renamed"; await group.RenameCommand.ExecuteAsync();
            Assert.Equal("Equipment", f.Dialogs.InitialInputText); Assert.Equal("Renamed", Assert.Single(profiles.Groups).Name);
            group = profiles.VisibleItems.OfType<ProfileGroupItem>().Single();
            await group.ToggleCommand.ExecuteAsync();
            await profiles.MoveModAsync(profiles.Entries.Single(row => row.Name == "Armor").Mod.Id,
                profiles.Entries.Single(row => row.Name == "Cape").Mod.Id, after: false);
            Assert.All(profiles.Entries, row => Assert.Equal(groupId, row.Entry.GroupId));
            Assert.Equal(new[] { "Armor", "Cape", "Helmet" }, profiles.Entries.Select(row => row.Name));
            await profiles.VisibleItems.OfType<ProfileGroupItem>().Single().RemoveCommand.ExecuteAsync();
            Assert.Empty(profiles.Groups); Assert.Equal(3, profiles.Entries.Count);
            Assert.All(profiles.Entries, row => Assert.Null(row.Entry.GroupId));
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task OptionPreviewsFollowSelectionAndAppearInDropdown()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); var source = f.OptionsSource();
        Directory.CreateDirectory(Path.Combine(source, "images"));
        foreach (var name in new[] { "default", "blue", "red" })
        {
            using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(640, 360), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var buffer = bitmap.Lock())
            {
                var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = name == "blue" ? (byte)210 : (byte)70;
                    pixels[index + 1] = 110; pixels[index + 2] = name == "red" ? (byte)210 : (byte)70; pixels[index + 3] = 255;
                }
                global::System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            }
            bitmap.Save(Path.Combine(source, "images", name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
            {"Version":1,"Name":"Preview variants","Options":[
              {"Name":"Color","Description":"Choose a color","Image":"images/default.png","Include":["common"],"SubOptions":[
                {"Name":"Blue","Description":"Blue preview","Image":"images/blue.png","Include":["blue"]},
                {"Name":"Red","Description":"Red preview","Image":"images/red.png","Include":["red"]},
                {"Name":"Nothing","Include":[]}]},
              {"Name":"Embedded","Image":"images/default.png","Include":["common"]},
              {"Name":"Unsafe","Image":"../outside.png","Include":["common"]}]}
            """);
        await f.Services.Session.ImportAsync(source, CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(box => box.Name == "ProfileModsList");
            list.ContextMenu!.Open(list); Dispatcher.UIThread.RunJobs();
            Assert.IsType<MenuItem>(list.ContextMenu.Items[0]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            window.CaptureRenderedFrame()?.Dispose();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<ModSettingsDialog>());
            var option = profiles.Options!.Options[0];
            var preview = dialog.GetVisualDescendants().OfType<ModPreviewImage>().Single(image => ReferenceEquals(image.DataContext, option));
            Assert.NotNull(preview.Source); Assert.EndsWith("blue.png", preview.FilePath);
            var enabled = dialog.GetVisualDescendants().OfType<CheckBox>().Single(check => ReferenceEquals(check.DataContext, option));
            Assert.True(enabled.TranslatePoint(new(0, 0), dialog)!.Value.X < preview.TranslatePoint(new(0, 0), dialog)!.Value.X);
            var embedded = dialog.GetVisualDescendants().OfType<ModPreviewImage>().Single(image => ReferenceEquals(image.DataContext, profiles.Options.Options[1]));
            Assert.NotNull(embedded.Source);
            Assert.False(dialog.GetVisualDescendants().OfType<ModPreviewImage>().Single(image => ReferenceEquals(image.DataContext, profiles.Options.Options[2])).IsVisible);
            var combo = dialog.GetVisualDescendants().OfType<ComboBox>().Single(control => ReferenceEquals(control.DataContext, option));
            var checkPosition = enabled.TranslatePoint(new(0, 0), dialog)!.Value;
            var comboPosition = combo.TranslatePoint(new(0, 0), dialog)!.Value;
            var imagePosition = preview.TranslatePoint(new(0, 0), dialog)!.Value;
            Assert.True(checkPosition.Y + enabled.Bounds.Height <= comboPosition.Y);
            Assert.True(comboPosition.X + combo.Bounds.Width <= imagePosition.X);
            if (Environment.GetEnvironmentVariable("QUARTERMASTER_GUI_SCREENSHOTS") is { } screenshotDirectory)
            {
                Directory.CreateDirectory(screenshotDirectory);
                using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(screenshotDirectory, "Options.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            combo.IsDropDownOpen = true; window.CaptureRenderedFrame()?.Dispose();
            var popup = combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single();
            var choices = popup.Child!.GetVisualDescendants().OfType<ModPreviewImage>().ToArray();
            Assert.Equal(3, choices.Length);
            Assert.Equal(2, choices.Count(image => image.Source is not null));
            Assert.Contains(choices, image => image.FilePath?.EndsWith("red.png") == true);
            var clickedChoice = choices.Single(image => image.FilePath?.EndsWith("red.png") == true);
            clickedChoice.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(ModPreviewImage.PreviewRequestedEvent));
            window.CaptureRenderedFrame()?.Dispose();
            var viewer = dialog.FindControl<ModImagePreview>("ImagePreview")!;
            Assert.True(viewer.IsVisible); Assert.False(combo.IsDropDownOpen);
            var fullImage = viewer.FindControl<ModPreviewImage>("FullImage")!;
            Assert.EndsWith("red.png", fullImage.FilePath); Assert.Equal(new PixelSize(640, 360), Assert.IsType<Avalonia.Media.Imaging.Bitmap>(fullImage.Source).PixelSize);
            Assert.Equal(0, option.ChoiceIndex); // Enlarging a dropdown image doesn't select it.
            Assert.Same(viewer.FindControl<Button>("BackButton"), window.FocusManager!.GetFocusedElement());
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(window.FindControl<DialogHost>("DialogOverlay")!.IsOpen);
            Assert.False(viewer.IsVisible); Assert.Null(fullImage.Source);
            combo.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()?.Dispose();
            var previewPoint = preview.TranslatePoint(new Point(preview.Bounds.Width / 2, preview.Bounds.Height / 2), window)!.Value;
            window.MouseMove(previewPoint); window.MouseDown(previewPoint, MouseButton.Left); window.MouseUp(previewPoint, MouseButton.Left);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(viewer.IsVisible); Assert.True(dialog.Width > 520); Assert.Empty(window.OwnedWindows);
            Assert.EndsWith("red.png", fullImage.FilePath);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(viewer.IsVisible); Assert.True(window.FindControl<DialogHost>("DialogOverlay")!.IsOpen);
            Assert.Equal(1, option.ChoiceIndex); Assert.Equal(520, dialog.Width);
            preview.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(viewer.IsVisible);
            viewer.FindControl<Button>("BackButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            Assert.EndsWith("red.png", preview.FilePath); Assert.NotNull(preview.Source);
            combo.SelectedIndex = 2; Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("default.png", preview.FilePath); // Fall back to option artwork.
            enabled.IsChecked = false; Assert.False(combo.IsEnabled);
            enabled.IsChecked = true; combo.SelectedIndex = 1;
            await profiles.ApplyOptionsCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError);
            Assert.Equal(1, Assert.Single(profiles.SelectedProfile!.Entries).Options[0].ChoiceIndex);
            dialog.Cancel(); Dispatcher.UIThread.RunJobs();
            Assert.Null(preview.Source); Assert.Null(embedded.Source);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ModIconBordersTrackEnabledStateAndActualDeploymentPerMod()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor", 2), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            void Check(string name, ModDeploymentState state, string color)
            {
                var item = profiles.Entries.Single(e => e.Name == name);
                Assert.Equal(state, item.DeploymentState);
                window.CaptureRenderedFrame()?.Dispose();
                var row = window.GetVisualDescendants().OfType<ModRowView>().Single(view => view.DataContext is ProfileModItem mod && mod.Name == name);
                var border = row.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("modTile"));
                Assert.Equal(Avalonia.Media.Color.Parse(color), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(border.BorderBrush).Color);
                Assert.Equal(item.DeploymentDescription, ToolTip.GetTip(border));
            }
            Check("Cape", ModDeploymentState.Warning, "#E2C457");
            profiles.SelectedMod = profiles.Entries.Single(e => e.Name == "Cape");
            await profiles.ToggleCommand.ExecuteAsync();
            Check("Cape", ModDeploymentState.Unloaded, "#E56A6A");
            await profiles.ToggleCommand.ExecuteAsync(); await profiles.DeployCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            Check("Cape", ModDeploymentState.Loaded, "#4AB98A");
            Check("Armor", ModDeploymentState.Loaded, "#4AB98A");
            f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
            profiles.ModToAdd = f.Services.Session.State.Mods.Single(m => m.Name == "Cape");
            await profiles.AddCommand.ExecuteAsync();
            Check("Cape", ModDeploymentState.Loaded, "#4AB98A"); // Loaded from the original profile.
            await profiles.ToggleCommand.ExecuteAsync();
            Check("Cape", ModDeploymentState.Warning, "#E2C457");
            await f.Shell.SidebarProfiles.Single(p => p.Name == "Default").SelectCommand.ExecuteAsync();
            var capeId = profiles.Entries.Single(e => e.Name == "Cape").Mod.Id;
            var owned = f.Services.Session.Inspection!.Ledger.Files.First(file => file.SourceId == capeId);
            File.WriteAllBytes(Path.Combine(f.Game, owned.Name), [0]);
            await f.Services.Session.ReloadAsync(CancellationToken.None);
            Check("Cape", ModDeploymentState.Warning, "#E2C457");
            Check("Armor", ModDeploymentState.Loaded, "#4AB98A");
            profiles.SelectedMod = profiles.Entries.Single(e => e.Name == "Cape");
            await profiles.ToggleCommand.ExecuteAsync();
            Check("Cape", ModDeploymentState.Warning, "#E2C457");
            await profiles.PurgeCommand.ExecuteAsync();
            Check("Cape", ModDeploymentState.Unloaded, "#E56A6A");
            Check("Armor", ModDeploymentState.Warning, "#E2C457");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AppliedOptionsTurnOutlineYellowEvenWhenChoicesUseIdenticalFiles()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var source = f.OptionsSource();
        File.WriteAllText(Path.Combine(source, "manifest.json"), """
            {"version":1,"name":"Same files","options":[{"name":"Appearance","include":["common"],"subOptions":[
              {"name":"First","include":["blue"]},{"name":"Second","include":["blue"]}]}]}
            """);
        await f.Services.Session.ImportAsync(source, CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        await profiles.DeployCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            void Check(ModDeploymentState state, string color)
            {
                Assert.Equal(state, Assert.Single(profiles.Entries).DeploymentState);
                window.CaptureRenderedFrame()?.Dispose();
                var border = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("modTile"));
                Assert.Equal(Avalonia.Media.Color.Parse(color), Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(border.BorderBrush).Color);
            }
            Check(ModDeploymentState.Loaded, "#4AB98A");
            var signature = f.Services.Session.Inspection!.Ledger.Signature;
            var initial = ProfilePatches.Resolve(f.Services.Session.State, profiles.SelectedProfile!);
            profiles.Options!.Options[0].ChoiceIndex = 1; await profiles.ApplyOptionsCommand.ExecuteAsync();
            var changed = ProfilePatches.Resolve(f.Services.Session.State, profiles.SelectedProfile!);
            Assert.Equal(initial.Patches.Select(patch => patch.PatchSetId), changed.Patches.Select(patch => patch.PatchSetId));
            Assert.NotEqual(signature, DeploymentPlanner.Create(changed).Signature);
            Check(ModDeploymentState.Warning, "#E2C457");
            await f.Services.Session.ReloadAsync(CancellationToken.None); Check(ModDeploymentState.Warning, "#E2C457");
            profiles.Options!.Options[0].ChoiceIndex = 0; await profiles.ApplyOptionsCommand.ExecuteAsync();
            Check(ModDeploymentState.Loaded, "#4AB98A");
            profiles.Options!.Options[0].ChoiceIndex = 1; await profiles.ApplyOptionsCommand.ExecuteAsync();
            await profiles.DeployCommand.ExecuteAsync(); Check(ModDeploymentState.Loaded, "#4AB98A");
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }
    [AvaloniaFact]
    public async Task ChangedOptionsAndIncompleteDeploymentShowWarningInsteadOfLoaded()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.OptionsSource(), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync();
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        await profiles.DeployCommand.ExecuteAsync();
        Assert.Equal(ModDeploymentState.Loaded, Assert.Single(profiles.Entries).DeploymentState);
        var mod = Assert.Single(f.Services.Session.State.Mods);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.SetOptions(profiles.SelectedProfile!, mod, [new(mod.Options[0].Id, true, 1)]), false, CancellationToken.None);
        Assert.Equal(ModDeploymentState.Warning, Assert.Single(profiles.Entries).DeploymentState);
        await File.WriteAllTextAsync(Path.Combine(f.Data, "deployment.lock"), "invalid");
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var unknown = Assert.Single(profiles.Entries);
        Assert.Equal(ModDeploymentState.Unknown, unknown.DeploymentState);
        Assert.True(unknown.HasDeploymentWarning); Assert.Contains("could not be verified", unknown.DeploymentDescription);
    }

    [AvaloniaFact]
    public async Task ManifestIconsCompactRowsAndLibraryAddToMenuWorkInBothViews()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var source = f.Source("Icon mod");
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """{"Version":1,"Name":"Icon mod","ModVersion":"v2","Description":"First paragraph.\n\nSecond paragraph.","IconPath":"icon.png"}""");
        using (var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul))
            bitmap.Save(Path.Combine(source, "icon.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        await f.Services.Session.ImportAsync(source, CancellationToken.None);
        var longSource = f.Source("Long mod");
        await File.WriteAllTextAsync(Path.Combine(longSource, "manifest.json"), global::System.Text.Json.JsonSerializer.Serialize(new
        { Version = 1, Description = string.Join(" ", Enumerable.Repeat("Long description that wraps.", 80)), IconPath = "bad.png" }));
        File.WriteAllText(Path.Combine(longSource, "bad.png"), "invalid image");
        await f.Services.Session.ImportAsync(longSource, CancellationToken.None);
        f.Dialogs.InputText = "Target"; await f.Shell.AddProfileCommand.ExecuteAsync();
        var targetId = f.Services.Session.State.ActiveProfileId;
        await f.Shell.SidebarProfiles.Single(p => p.Name == "Default").SelectCommand.ExecuteAsync();
        var activeId = f.Services.Session.State.ActiveProfileId;
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ModsView>());
            var list = view.FindControl<ListBox>("ModsList")!;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is ModListItem { Name: "Icon mod" });
            var contents = Assert.Single(row.GetVisualDescendants().OfType<ModRowView>());
            Assert.NotNull(contents.FindControl<Image>("ModIcon")!.Source);
            Assert.False(contents.FindControl<TextBlock>("ModMonogram")!.IsVisible);
            Assert.Contains(contents.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Icon mod · v2");
            Assert.Contains(contents.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "First paragraph. Second paragraph.");
            var longRow = list.GetVisualDescendants().OfType<ListBoxItem>().Single(item => item.DataContext is ModListItem { Name: "Long mod" });
            Assert.True(longRow.Bounds.Height > row.Bounds.Height);
            Assert.True(Assert.Single(longRow.GetVisualDescendants().OfType<ModRowView>()).FindControl<TextBlock>("ModMonogram")!.IsVisible);
            var point = row.TranslatePoint(new Point(240, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right);
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Icon mod", mods.SelectedMod!.Name);
            var menu = list.ContextMenu!; if (!menu.IsOpen) menu.Open(list);
            var addTo = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Add to"));
            var target = addTo.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Target"));
            await Assert.IsType<AsyncCommand>(target.Command).ExecuteAsync(); menu.Close();
            Assert.Equal(activeId, f.Services.Session.State.ActiveProfileId);
            Assert.Equal(mods.SelectedMod!.Mod.Id, Assert.Single(f.Services.Session.State.Profiles.Single(p => p.Id == targetId).Entries).ModId);
            Assert.Equal(2, f.Services.Session.State.Mods.Count);
            menu.Open(list); Dispatcher.UIThread.RunJobs();
            target = addTo.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Target"));
            Assert.False(target.Command!.CanExecute(null)); menu.Close();
            await f.Shell.SidebarProfiles.Single(p => p.Name == "Target").SelectCommand.ExecuteAsync();
            window.CaptureRenderedFrame()?.Dispose();
            var profileRow = Assert.Single(window.GetVisualDescendants().OfType<ModRowView>());
            Assert.NotNull(profileRow.FindControl<Image>("ModIcon")!.Source);
            Assert.DoesNotContain(Directory.GetFiles(f.Game), path => path.Contains(".patch_"));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(PageKind.Mods)]
    [InlineData(PageKind.Profiles)]
    public async Task BlankListAreaAcceptsFileDropsWhenEmptyOrPartiallyFilled(PageKind page)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Shell.Navigate(page);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            for (var index = 0; index < 3; index++)
            {
                window.CaptureRenderedFrame()?.Dispose();
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == (page == PageKind.Mods ? "ModsList" : "ProfileModsList"));
                // Stay far below any rows or empty-list message.
                var position = list.TranslatePoint(new Point(list.Bounds.Width - 24, list.Bounds.Height - 24), window)!.Value;
                if (index == 2)
                {
                    var view = list.GetVisualAncestors().OfType<UserControl>().First();
                    var header = Assert.IsType<Grid>(Assert.IsType<Grid>(view.Content).Children[0]);
                    position = header.TranslatePoint(new Point(header.Bounds.Width - 24, header.Bounds.Height + 4), window)!.Value;
                }
                var file = await window.StorageProvider.TryGetFileFromPathAsync(new Uri(f.Zip("Dropped " + index)));
                var data = new DataTransfer(); data.Add(DataTransferItem.CreateFile(file!));
                window.DragDrop(position, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(position, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                window.DragDrop(position, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
                await f.Services.Operations.WhenIdle;
                await f.Services.Operations.WhenIdle;
                Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
                Assert.Equal(index + 1, f.Services.Session.State.Mods.Count);
                Assert.Equal(page == PageKind.Profiles ? index + 1 : 0, Assert.Single(f.Services.Session.State.Profiles).Entries.Count);
            }
        }
        finally { await f.Services.Operations.WhenIdle; window.Close(); }
    }

    [AvaloniaFact]
    public async Task FileDropsImportIntoLibraryAndSpecifiedProfileWithoutDeploying()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            async Task Drop(Control target, string[] paths, bool accepted = true)
            {
                window.CaptureRenderedFrame()?.Dispose();
                var data = new DataTransfer();
                foreach (var path in paths)
                {
                    IStorageItem? item = Directory.Exists(path)
                        ? await window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path))
                        : await window.StorageProvider.TryGetFileFromPathAsync(new Uri(path));
                    data.Add(DataTransferItem.CreateFile(Assert.IsAssignableFrom<IStorageItem>(item)));
                }
                var position = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
                var host = window.FindControl<Grid>("ShellContent")!;
                var over = new DragEventArgs(DragDrop.DragOverEvent, data, window, position, KeyModifiers.None);
                host.RaiseEvent(over);
                Assert.Equal(accepted ? DragDropEffects.Copy : DragDropEffects.None, over.DragEffects);
                host.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, window, position, KeyModifiers.None));
                await f.Services.Operations.WhenIdle;
                // Profile navigation follows the completed import operation.
                await f.Services.Operations.WhenIdle;
                Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            }
            Page<ModsViewModel>(f, PageKind.Mods); window.CaptureRenderedFrame()?.Dispose();
            await Drop(Assert.Single(window.GetVisualDescendants().OfType<ModsView>()), [f.Zip("Library ZIP"), f.Source("Library folder")]);
            Assert.Equal(2, f.Services.Session.State.Mods.Count);
            Assert.Empty(Assert.Single(f.Services.Session.State.Profiles).Entries);
            f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
            var targetId = f.Services.Session.State.ActiveProfileId;
            var defaultProfile = f.Shell.SidebarProfiles.Single(p => p.Name == "Default");
            await defaultProfile.SelectCommand.ExecuteAsync();
            window.CaptureRenderedFrame()?.Dispose();
            var other = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is SidebarProfile { Name: "Other" });
            await Drop(other, [f.Source("Profile folder")]);
            Assert.Equal(targetId, f.Services.Session.State.ActiveProfileId);
            Assert.Single(f.Services.Session.State.Profiles.Single(p => p.Id == targetId).Entries);
            window.CaptureRenderedFrame()?.Dispose();
            await Drop(Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>()), [f.Zip("Profile ZIP")]);
            Assert.Equal(2, f.Services.Session.State.Profiles.Single(p => p.Id == targetId).Entries.Count);
            Assert.Empty(f.Services.Session.State.Profiles.Single(p => p.Name == "Default").Entries);
            var unsupported = Path.Combine(f.Root, "readme.txt"); File.WriteAllText(unsupported, "unsupported");
            await Drop(Assert.Single(window.GetVisualDescendants().OfType<ProfilesView>()), [unsupported], accepted: false);
            Page<SettingsViewModel>(f, PageKind.Settings); window.CaptureRenderedFrame()?.Dispose();
            await Drop(Assert.Single(window.GetVisualDescendants().OfType<SettingsView>()), [f.Source("Rejected")], accepted: false);
            var libraryButton = window.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is NavigationItem { Page: PageKind.Mods });
            await Drop(libraryButton, [f.Zip("Sidebar library")]);
            Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
            Assert.Equal(5, f.Services.Session.State.Mods.Count);
            Assert.DoesNotContain(Directory.GetFiles(f.Game), path => path.Contains(".patch_"));
        }
        finally { await f.Services.Operations.WhenIdle; window.Close(); }
    }

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
                (PageKind.Search, typeof(SearchView)), (PageKind.Downloads, typeof(DownloadsView)), (PageKind.Settings, typeof(SettingsView))
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
                    Directory.CreateDirectory(output); frame.Save(Path.Combine(output, page + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
    public async Task LibraryUsesFullWidthListWithDetailsDialogAndContextActions()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        var mods = Page<ModsViewModel>(f, PageKind.Mods);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<ModsView>());
            var list = view.FindControl<ListBox>("ModsList")!;
            Assert.Equal(view.Bounds.Width, list.Bounds.Width, 1);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ModDetailsDialog>(), _ => true);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Content is "Export repatched ZIP" or "Remove selected mod");
            var row = Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>(), item => item.DataContext is ModListItem { Name: "Cape" });
            var point = row.TranslatePoint(new Point(240, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, Avalonia.Input.MouseButton.Right); window.MouseUp(point, Avalonia.Input.MouseButton.Right);
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Cape", mods.SelectedMod!.Name);
            var menu = list.ContextMenu!; if (!menu.IsOpen) menu.Open(list);
            var details = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Mod details"));
            details.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            window.CaptureRenderedFrame()?.Dispose();
            var dialog = Assert.Single(window.GetVisualDescendants().OfType<ModDetailsDialog>());
            Assert.Equal("Cape", Assert.IsType<ModDetailsViewModel>(dialog.DataContext).Name);
            Assert.Empty(window.OwnedWindows); dialog.Cancel(); Dispatcher.UIThread.RunJobs();
            menu.Open(list); Dispatcher.UIThread.RunJobs();
            await Assert.IsType<AsyncCommand>(menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Export repatched ZIP")).Command).ExecuteAsync(); menu.Close();
            Assert.Equal("Cape-repatched.zip", f.Dialogs.SuggestedSaveName);
            f.Dialogs.Confirm = false; menu.Open(list); Dispatcher.UIThread.RunJobs();
            var remove = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Remove from library"));
            await Assert.IsType<AsyncCommand>(remove.Command).ExecuteAsync(); Assert.Equal(2, mods.Mods.Count);
            f.Dialogs.Confirm = true; await Assert.IsType<AsyncCommand>(remove.Command).ExecuteAsync(); menu.Close();
            Assert.Equal("Armor", Assert.Single(mods.Mods).Name);
            mods.Search = "missing"; Assert.False(mods.HasVisibleMods); Assert.Equal("No mods match your search.", mods.EmptyMessage);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileContextMenuTargetsClickedModAndDragDropPersistsOrder()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" })
            await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles);
        await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), b => b.Content is "Move up" or "Move down" or "Mod details" or "Remove from profile" or "Purge patches");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Purge"));
            var list = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), box => box.Name == "ProfileModsList");
            ListBoxItem Row(string name) => Assert.Single(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.DataContext is ProfileModItem item && item.Name == name);
            Point Position(ListBoxItem row, double y) => row.TranslatePoint(new Point(240, y), window)!.Value;
            var cape = Row("Cape"); var point = Position(cape, cape.Bounds.Height / 2);
            window.MouseMove(point); window.MouseDown(point, Avalonia.Input.MouseButton.Right); window.MouseUp(point, Avalonia.Input.MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Cape", profiles.SelectedMod!.Name);
            var menu = list.ContextMenu!;
            if (!menu.IsOpen) menu.Open(list);
            var toggle = Assert.IsType<MenuItem>(menu.Items[1]);
            await Assert.IsType<AsyncCommand>(toggle.Command).ExecuteAsync(); menu.Close();
            Assert.False(profiles.Entries.Single(row => row.Name == "Cape").IsEnabled);
            window.CaptureRenderedFrame()?.Dispose();
            var armor = Row("Armor"); var helmet = Row("Helmet");
            var from = Position(armor, armor.Bounds.Height / 2); var to = Position(helmet, helmet.Bounds.Height - 4);
            window.MouseMove(from); window.MouseDown(from, Avalonia.Input.MouseButton.Left);
            window.MouseMove(to); window.CaptureRenderedFrame()?.Dispose();
            Assert.Contains("dropAfter", helmet.Classes);
            window.MouseUp(to, Avalonia.Input.MouseButton.Left);
            await f.Services.Operations.WhenIdle; Dispatcher.UIThread.RunJobs();
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
            Assert.Equal(new[] { "Cape", "Helmet", "Armor" }, profiles.Entries.Select(row => row.Name));
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            Assert.Equal(profiles.Entries.Select(row => row.Mod.Id), persisted.Profiles.Single().Entries.Select(entry => entry.ModId));
            profiles.Search = "e"; window.CaptureRenderedFrame()?.Dispose();
            cape = Row("Cape"); helmet = Row("Helmet");
            from = Position(cape, cape.Bounds.Height / 2); to = Position(helmet, helmet.Bounds.Height - 4);
            window.MouseMove(from); window.MouseDown(from, Avalonia.Input.MouseButton.Left); window.MouseMove(to);
            window.MouseUp(to, Avalonia.Input.MouseButton.Left); await f.Services.Operations.WhenIdle;
            Assert.Equal(new[] { "Helmet", "Cape", "Armor" }, profiles.Entries.Select(row => row.Name));
            window.CaptureRenderedFrame()?.Dispose();
            cape = Row("Cape"); helmet = Row("Helmet");
            from = Position(cape, cape.Bounds.Height / 2); to = Position(helmet, 4);
            window.MouseMove(from); window.MouseDown(from, Avalonia.Input.MouseButton.Left); window.MouseMove(to);
            list.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            window.MouseUp(to, Avalonia.Input.MouseButton.Left);
            Assert.Equal(new[] { "Helmet", "Cape", "Armor" }, profiles.Entries.Select(row => row.Name));
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<ListBoxItem>(), row => row.Classes.Contains("dropBefore") || row.Classes.Contains("dropAfter"));
            profiles.Search = "Armor"; window.CaptureRenderedFrame()?.Dispose();
            var original = profiles.Entries.Select(row => row.Mod.Id).ToArray();
            // Dropping on the same row is a no-op, including in a filtered view.
            await profiles.MoveModAsync(original[2], original[2], after: false);
            Assert.Equal(original, profiles.Entries.Select(row => row.Mod.Id));
            profiles.Search = "";
            await profiles.MoveModAsync(original[2], original[0], after: false);
            await profiles.MoveModAsync(original[1], original[0], after: false);
            Assert.Equal(new[] { "Armor", "Cape", "Helmet" }, profiles.Entries.Select(row => row.Name));
            profiles.SelectedMod = profiles.Entries.Single(row => row.Name == "Helmet");
            menu.Open(list); Dispatcher.UIThread.RunJobs();
            var remove = Assert.IsType<MenuItem>(menu.Items[3]);
            await Assert.IsType<AsyncCommand>(remove.Command).ExecuteAsync(); menu.Close();
            Assert.DoesNotContain(profiles.Entries, row => row.Name == "Helmet");
        }
        finally { window.Close(); }
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
            var reopened = f.ReopenServices();
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
            var persisted = f.ReopenServices();
            await persisted.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal("Second loadout", persisted.Session.ActiveProfile!.Name);
            Assert.Equal(4, persisted.Session.State.Profiles.Count);
            Dispatcher.UIThread.RunJobs(); window.CaptureRenderedFrame()?.Dispose();
            Assert.DoesNotContain(window.GetVisualDescendants(), control => control is Expander);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileContextMenuDuplicatesClickedProfileWithIndependentGroupsAndOptions()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.OptionsSource(), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        var variants = f.Services.Session.State.Mods.Single(mod => mod.Options.Count > 0);
        var cape = f.Services.Session.State.Mods.Single(mod => mod.Options.Count == 0);
        var original = ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, variants), cape);
        original = ProfileEditor.SetOptions(original, variants, [new(variants.Options[0].Id, true, 1)]);
        original = ProfileEditor.SetEnabled(original, cape.Id, false);
        original = ProfileEditor.AddGroup(original, "Equipment", [variants.Id]);
        original = ProfileEditor.SetGroupExpanded(original, original.Groups[0].Id, false) with { Priority = PriorityDirection.FirstWins };
        await f.Services.Session.SaveProfileAsync(original, true, CancellationToken.None);
        f.Dialogs.InputText = "Other"; await f.Shell.AddProfileCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var button = window.GetVisualDescendants().OfType<Button>().Single(item => item.DataContext is SidebarProfile profile && profile.Profile.Id == original.Id);
            var menu = button.ContextMenu!; menu.Open(button); Dispatcher.UIThread.RunJobs();
            var duplicate = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Duplicate profile"));
            await Assert.IsType<AsyncCommand>(duplicate.Command).ExecuteAsync(); menu.Close();
            Assert.False(f.Services.Operations.IsError);
            var copy = f.Services.Session.ActiveProfile!;
            Assert.Equal("Default (copy)", copy.Name); Assert.NotEqual(original.Id, copy.Id);
            Assert.Equal(original.Priority, copy.Priority);
            Assert.Equal(original.Entries.Select(entry => (entry.ModId, entry.Enabled)), copy.Entries.Select(entry => (entry.ModId, entry.Enabled)));
            Assert.Equal(original.Entries.Single(entry => entry.ModId == variants.Id).Options, copy.Entries.Single(entry => entry.ModId == variants.Id).Options);
            var group = Assert.Single(copy.Groups);
            Assert.Equal("Equipment", group.Name); Assert.False(group.IsExpanded); Assert.NotEqual(original.Groups[0].Id, group.Id);
            Assert.Equal(group.Id, copy.Entries.Single(entry => entry.ModId == variants.Id).GroupId);
            Assert.Equal(PageKind.Profiles, f.Shell.SelectedNavigation.Page);
            Assert.Equal(copy.Id, Assert.Single(f.Shell.SidebarProfiles, profile => profile.IsActive).Profile.Id);
            Assert.Equal(2, f.Services.Session.State.Mods.Count);
            Assert.Empty(f.Services.Session.Inspection!.Ledger.Files);
            var changed = ProfileEditor.SetOptions(ProfileEditor.RenameGroup(copy, group.Id, "Changed"), variants, [new(variants.Options[0].Id, false, 0)]);
            await f.Services.Session.SaveProfileAsync(changed, true, CancellationToken.None);
            var persisted = await new Quartermaster.Library.Storage.JsonLibraryStore(f.Data).LoadAsync();
            var source = persisted.Profiles.Single(profile => profile.Id == original.Id);
            Assert.Equal("Equipment", source.Groups[0].Name);
            Assert.Equal(1, Assert.Single(source.Entries.Single(entry => entry.ModId == variants.Id).Options).ChoiceIndex);
            Assert.Equal("Default (copy)", persisted.Profiles.Single(profile => profile.Id == copy.Id).Name);
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
            var delete = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Delete"));
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
                    Directory.CreateDirectory(output); frame.Save(Path.Combine(output, "ProfileDialog.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
        profiles.SelectedMod = profiles.Entries[1]; await profiles.MoveModAsync(profiles.SelectedMod!.Mod.Id, profiles.Entries[0].Mod.Id, after: false);
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
        var reopened = f.ReopenServices(); await reopened.Session.InitializeAsync(CancellationToken.None);
        Assert.Equal(2, reopened.Session.State.Mods.Count); Assert.Equal(2, reopened.Session.ActiveProfile!.Entries.Count);
        Assert.Equal(f.Game, reopened.Session.GameDirectory); Assert.Equal(4, reopened.Session.Inspection!.Ledger.Files.Count);
        f.Dialogs.Confirm = false; await profiles.PurgeCommand.ExecuteAsync(); Assert.True(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
        File.WriteAllBytes(foreign, [0xee]);
        f.Dialogs.Confirm = true; await profiles.PurgeCommand.ExecuteAsync();
        Assert.Empty(f.Services.Session.Inspection!.Ledger.Files); Assert.False(File.Exists(foreign));
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
    }

    [AvaloniaFact]
    public async Task DeploymentProgressShowsModCountAndCurrentName()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Cape", 1), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor", 2), CancellationToken.None);
        var profile = f.Services.Session.ActiveProfile!;
        foreach (var mod in f.Services.Session.State.Mods) profile = ProfileEditor.Add(profile, mod);
        await f.Services.Session.SaveProfileAsync(profile, true, CancellationToken.None);
        await f.Services.Session.SaveSettingsAsync(f.Game, RepatchMode.Never, null, profile.Priority, CancellationToken.None);
        var messages = new List<string>();
        f.Services.Operations.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(OperationState.Message)) messages.Add(f.Services.Operations.Message); };
        await Page<ProfilesViewModel>(f, PageKind.Profiles).DeployCommand.ExecuteAsync(); Dispatcher.UIThread.RunJobs();
        Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        var names = ProfileEditor.InDeploymentOrder(profile).Select(entry => f.Services.Session.State.Mods.Single(mod => mod.Id == entry.ModId).Name).ToArray();
        Assert.Contains($"Deploying 1 of 2: {names[0]}", messages);
        Assert.Contains($"Deploying 2 of 2: {names[1]}", messages);
        Assert.Equal("Deploying profile complete", f.Services.Operations.Message);
    }
    [AvaloniaFact]
    public async Task QueuedProgressCannotOverwriteLaterOperations()
    {
        var operations = new OperationState();
        await operations.RunAsync("First", _ =>
        {
            operations.CreateProgress<string>(value => value).Report("Old progress");
            return Task.CompletedTask;
        });
        await operations.RunAsync("Second", _ =>
        {
            Dispatcher.UIThread.RunJobs(); Assert.Equal("Second", operations.Message);
            return Task.CompletedTask;
        });
        Assert.Equal("Second complete", operations.Message);
    }
    [AvaloniaFact]
    public async Task ModOptionControlsSaveSelectedVariantToLibrary()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        f.Dialogs.FolderPath = f.OptionsSource(); await Page<ModsViewModel>(f, PageKind.Mods).ImportFolderCommand.ExecuteAsync();
        await f.Services.Session.ImportAsync(f.Source("Plain mod"), CancellationToken.None);
        var profiles = Page<ProfilesViewModel>(f, PageKind.Profiles); await profiles.AddCommand.ExecuteAsync(); await profiles.AddCommand.ExecuteAsync();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var option = Assert.Single(profiles.Options!.Options);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == option);
            var list = Assert.Single(window.GetVisualDescendants().OfType<ListBox>(), box => box.Name == "ProfileModsList");
            var order = profiles.SelectedProfile!.Entries.Select(e => e.ModId).ToArray();
            var optionRow = list.GetVisualDescendants().OfType<ModRowView>().Single(row => row.DataContext is ProfileModItem { HasOptions: true });
            var plainRow = list.GetVisualDescendants().OfType<ModRowView>().Single(row => row.DataContext is ProfileModItem { HasOptions: false });
            Assert.False(plainRow.FindControl<Button>("ModOptionsButton")!.IsEnabled);
            profiles.SelectedMod = profiles.Entries.Single(item => item.Name == "Plain mod");
            var button = optionRow.FindControl<Button>("ModOptionsButton")!;
            Assert.True(button.IsVisible); Assert.True(button.IsEnabled);
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Assert.Equal("Armor variants", profiles.SelectedMod!.Name);
            option = Assert.Single(profiles.Options!.Options);
            Dispatcher.UIThread.RunJobs();
            var popup = Assert.Single(window.GetVisualDescendants().OfType<ModSettingsDialog>());
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Empty(window.OwnedWindows);
            Assert.False(window.FindControl<Grid>("ShellContent")!.IsEnabled);
            var choice = Assert.Single(popup.GetVisualDescendants().OfType<ComboBox>(), c => c.DataContext == option);
            choice.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, option.ChoiceIndex);
            await profiles.ApplyOptionsCommand.ExecuteAsync();
            var state = f.Services.Session.State; var mod = state.Mods.Single(m => m.Options.Count > 0);
            var entry = profiles.SelectedProfile!.Entries.Single(e => e.ModId == mod.Id);
            Assert.Equal(order, profiles.SelectedProfile.Entries.Select(e => e.ModId));
            Assert.Equal(new[] { "common", "red" }, PatchSelection.Select(mod, entry).Select(p => p.Folder));
            var reopened = f.ReopenServices(); await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal(1, Assert.Single(reopened.Session.ActiveProfile!.Entries.Single(e => e.ModId == mod.Id).Options).ChoiceIndex);
            popup.Cancel(); Dispatcher.UIThread.RunJobs();
            f.Shell.Navigate(PageKind.Mods); window.CaptureRenderedFrame()?.Dispose();
            Assert.All(window.GetVisualDescendants().OfType<ModRowView>(), row => Assert.False(row.FindControl<Button>("ModOptionsButton")!.IsVisible));
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
        settings.RepatchChoice = (int)RepatchMode.Automatic; await settings.SaveCommand.ExecuteAsync();
        f.Dialogs.Confirmations.Clear(); await profiles.DeployCommand.ExecuteAsync();
        Assert.DoesNotContain(f.Dialogs.Confirmations, c => c.Title == "Repatch required");
        Assert.False(f.Services.Operations.IsError);
        var saved = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
        Assert.Equal(RepatchMode.Automatic, saved.Repatch);
        settings.RepatchChoice = (int)RepatchMode.Never; await settings.SaveCommand.ExecuteAsync();
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
    public async Task SettingsUsesOneSaveForDraftsAndSearchDoesNotDiscardEdits()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var settings = Page<SettingsViewModel>(f, PageKind.Settings);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = Assert.Single(window.GetVisualDescendants().OfType<SettingsView>());
            var save = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Save"));
            Assert.False(save.IsEffectivelyEnabled);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Content is "Save folder" or "Save repatch setting" or "Purge patches" or "Use selected installation");
            var nextGame = Path.Combine(f.Root, "second-game"); Directory.CreateDirectory(nextGame);
            File.WriteAllBytes(Path.Combine(nextGame, Fixture.Archive), []);
            f.Dialogs.FolderPath = nextGame; await settings.BrowseCommand.ExecuteAsync();
            var repatch = Assert.Single(view.FindControl<Grid>("RepatchSettings")!.GetVisualDescendants().OfType<ComboBox>());
            var priority = Assert.Single(view.FindControl<Grid>("PrioritySettings")!.GetVisualDescendants().OfType<ComboBox>());
            repatch.SelectedIndex = (int)RepatchMode.Automatic; priority.SelectedIndex = (int)PriorityDirection.FirstWins;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(f.Game, f.Services.Session.GameDirectory);
            Assert.Equal(RepatchMode.Ask, f.Services.Session.Settings.Repatch);
            Assert.Equal(PriorityDirection.LastWins, f.Services.Session.ActiveProfile!.Priority);
            settings.Search = "repatch"; window.CaptureRenderedFrame()?.Dispose();
            Assert.True(view.FindControl<Grid>("RepatchSettings")!.IsEffectivelyVisible);
            Assert.False(view.FindControl<Grid>("InstallationSettings")!.IsEffectivelyVisible);
            await f.Services.Session.ReloadAsync(CancellationToken.None);
            Assert.Equal(nextGame, settings.GamePath); Assert.Equal((int)RepatchMode.Automatic, settings.RepatchChoice);
            settings.GamePath = Path.Combine(f.Root, "missing"); await settings.SaveCommand.ExecuteAsync();
            Assert.True(f.Services.Operations.IsError);
            Assert.Equal(RepatchMode.Ask, f.Services.Session.Settings.Repatch);
            Assert.Equal(PriorityDirection.LastWins, f.Services.Session.ActiveProfile!.Priority);
            settings.GamePath = nextGame; await settings.SaveCommand.ExecuteAsync();
            Assert.False(f.Services.Operations.IsError); Assert.False(settings.SaveCommand.CanExecute(null));
            var stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Equal(nextGame, stored.GameDataDirectory); Assert.Equal(RepatchMode.Automatic, stored.Repatch);
            Assert.Equal(PriorityDirection.FirstWins, f.Services.Session.ActiveProfile!.Priority);
            var reset = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => Equals(b.Content, "Reset"));
            reset.Command!.Execute(null);
            Assert.Empty(settings.GamePath); Assert.Equal((int)RepatchMode.Ask, settings.RepatchChoice);
            Assert.Equal((int)PriorityDirection.LastWins, settings.PriorityChoice);
            Assert.Equal(nextGame, f.Services.Session.GameDirectory); Assert.Equal(RepatchMode.Automatic, f.Services.Session.Settings.Repatch);
            Assert.True(settings.SaveCommand.CanExecute(null));
            await settings.SaveCommand.ExecuteAsync();
            stored = await new SettingsStore(f.Data).LoadAsync(CancellationToken.None);
            Assert.Null(stored.GameDataDirectory); Assert.Equal(RepatchMode.Ask, stored.Repatch);
            Assert.Equal(PriorityDirection.LastWins, f.Services.Session.ActiveProfile!.Priority);
            Assert.Single(f.Services.Session.State.Profiles);
            settings.Search = "no matching setting"; Assert.False(settings.HasMatches);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InvalidSettingsAreReportedAndValidSettingsPersist()
    {
        using var f = new Fixture(discoverGame: false); await f.Shell.InitializeAsync();
        var settings = Page<SettingsViewModel>(f, PageKind.Settings);
        settings.GamePath = Path.Combine(f.Root, "missing"); await settings.SaveCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Empty(f.Services.Session.GameDirectory);
        settings.GamePath = f.Game; await settings.SaveCommand.ExecuteAsync();
        Assert.False(f.Services.Operations.IsError); Assert.Equal(f.Game, f.Services.Session.GameDirectory);
        await settings.DiscoverCommand.ExecuteAsync(); Assert.Empty(settings.Installations);
        Assert.Equal("Default", settings.ActiveProfileName);
        var previousPriority = f.Services.Session.ActiveProfile!.Priority;
        settings.PriorityChoice = 1 - settings.PriorityChoice; await settings.SaveCommand.ExecuteAsync();
        Assert.NotEqual(previousPriority, f.Services.Session.ActiveProfile!.Priority);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.NotEqual(previousPriority, f.Services.Session.ActiveProfile!.Priority);
        settings.PriorityChoice = 1 - settings.PriorityChoice; await settings.SaveCommand.ExecuteAsync();
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
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var overlay = window.FindControl<ErrorNotification>("ErrorOverlay")!;
            Assert.Equal(f.Services.Operations.Message, overlay.FindControl<TextBlock>("OperationMessage")!.Text);
            var bounds = window.FindControl<ContentControl>("PageHost")!.Bounds;
            f.Services.Operations.DismissErrorCommand.Execute(null);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.False(overlay.IsVisible);
            Assert.True(f.Services.Operations.IsError);
            Assert.Equal(bounds, window.FindControl<ContentControl>("PageHost")!.Bounds);
        }
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
            var reopened = f.ReopenServices();
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
