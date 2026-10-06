using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class ProfileRefreshTests
{
    [AvaloniaFact]
    public async Task MovingAModDoesNotDisableControlsOrInvalidateUnchangedRowBindings()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Armor", "Cape", "Helmet" })
            await f.Services.Session.ImportAsync(f.Source(name), CancellationToken.None);
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        for (var index = 0; index < 3; index++) await model.AddCommand.ExecuteAsync();
        f.Dialogs.InputText = "Equipment"; await model.AddGroupCommand.ExecuteAsync();
        var group = model.VisibleItems.OfType<ProfileGroupItem>().Single();
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var controls = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyEnabled).ToArray();
            var disabledControls = new List<Button>();
            foreach (var button in controls)
                button.PropertyChanged += (_, change) =>
                {
                    if (change.Property.Name is "IsEnabled" or "IsEffectivelyEnabled" && button.DataContext is not null && !button.IsEffectivelyEnabled)
                        disabledControls.Add(button);
                };
            model.VisibleItems.CollectionChanged += (_, _) =>
            {
                window.CaptureRenderedFrame()?.Dispose();
                Assert.All(window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("rowSwitch")),
                    button => Assert.True(button.IsEffectivelyEnabled));
            };
            var changes = new List<string?>();
            foreach (var row in model.Entries) row.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
            group.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
            var enabledCommands = model.Entries.Select(row => row.EnableCommand).ToArray();
            var protectedWhileSaving = false;
            f.Services.Session.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName != nameof(Services.LibrarySession.State)) return;
                protectedWhileSaving = true;
                Assert.True(f.Services.Operations.IsBusy);
                Assert.False(model.AddCommand.CanExecute(null));
                Assert.All(controls, button => Assert.True(button.IsEffectivelyEnabled));
                var list = window.GetVisualDescendants().OfType<ProfilesView>().Single().FindControl<ListBox>("ProfileModsList")!;
                Assert.False(list.IsEffectivelyVisible && list.GetVisualAncestors().OfType<Control>().All(control => control.IsHitTestVisible));
            };
            await model.MoveModAsync(model.Entries[2].Mod.Id, model.Entries[1].Mod.Id, false);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(protectedWhileSaving);
            Assert.True(disabledControls.Count == 0, string.Join("; ", disabledControls.Select(button => $"{button.Name}: {button.Content} [{string.Join(",", button.Classes)}] {button.DataContext?.GetType().Name}")));
            Assert.NotEmpty(changes);
            Assert.All(changes, property => Assert.Equal(nameof(ProfileModItem.Number), property));
            Assert.All(model.Entries, row => Assert.Contains(row.EnableCommand, enabledCommands));
            Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ProfileEditsPreserveListsSelectionsAndScrollPositions()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        for (var index = 0; index < 30; index++)
            await f.Services.Session.ImportAsync(f.Source($"Mod {index:00}"), CancellationToken.None);
        var profile = f.Services.Session.ActiveProfile!;
        foreach (var mod in f.Services.Session.State.Mods.Take(28)) profile = ProfileEditor.Add(profile, mod);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        f.Shell.Navigate(PageKind.Mods);
        var library = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
        var librarySource = library.Mods;
        var libraryRow = library.Mods[12];
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var libraryList = window.GetVisualDescendants().OfType<ModsView>().Single().FindControl<ListBox>("ModsList")!;
            library.SelectedMod = libraryRow;
            library.SelectedMods.Add(library.Mods[13]);
            var librarySelection = library.SelectedMods.ToArray();
            var libraryScroll = libraryList.GetVisualDescendants().OfType<ScrollViewer>().Single();
            libraryScroll.Offset = new Vector(0, 400);
            window.CaptureRenderedFrame()?.Dispose();
            var libraryOffset = libraryScroll.Offset;
            await library.AddSelectedToProfileAsync(profile.Id);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Same(librarySource, libraryList.ItemsSource);
            Assert.Same(libraryRow, library.Mods[12]);
            Assert.Equal(librarySelection, library.SelectedMods);
            Assert.Equal(libraryOffset, libraryScroll.Offset);

            f.Shell.Navigate(PageKind.Profiles);
            var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
            window.CaptureRenderedFrame()?.Dispose();
            var list = window.GetVisualDescendants().OfType<ProfilesView>().Single().FindControl<ListBox>("ProfileModsList")!;
            var source = model.VisibleItems;
            var row = model.Entries[12];
            list.SelectedItem = row;
            list.SelectedItems!.Add(model.Entries[13]);
            var selection = list.SelectedItems.Cast<object>().ToArray();
            var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 400);
            window.CaptureRenderedFrame()?.Dispose();
            var offset = scroll.Offset;
            Assert.True(offset.Y > 0);
            var resets = 0;
            source.CollectionChanged += (_, change) => { if (change.Action == NotifyCollectionChangedAction.Reset) resets++; };
            async Task Check(Func<Task> edit)
            {
                await edit(); window.CaptureRenderedFrame()?.Dispose();
                Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
                Assert.Same(source, list.ItemsSource);
                Assert.Equal(offset, scroll.Offset);
                Assert.Contains(row, model.Entries);
            }
            await Check(() => model.AddCommand.ExecuteAsync());
            Assert.Equal(selection, list.SelectedItems.Cast<object>());
            await Check(() => model.MoveModAsync(model.Entries[24].Mod.Id, model.Entries[22].Mod.Id, false));
            f.Dialogs.InputText = "First group";
            await Check(() => model.AddGroupCommand.ExecuteAsync());
            var group = model.VisibleItems.OfType<ProfileGroupItem>().Single();
            f.Dialogs.InputText = "Second group";
            await Check(() => model.AddGroupCommand.ExecuteAsync());
            await Check(() => model.MoveGroupAsync(group.Id, model.Groups.Last().Id, true));
            Assert.Same(group, model.VisibleItems.OfType<ProfileGroupItem>().Single(item => item.Id == group.Id));
            await Check(() => model.MoveModToGroupAsync(model.Entries[24].Mod.Id, group.Id));
            await Check(() => group.RemoveCommand.ExecuteAsync());
            model.SelectedMod = model.Entries.Last();
            await Check(() => model.RemoveCommand.ExecuteAsync());
            Assert.Equal(0, resets);
            // Commands on a retained row must use its latest enabled state.
            await row.EnableCommand.ExecuteAsync();
            Assert.False(row.IsEnabled);
            await row.EnableCommand.ExecuteAsync();
            Assert.True(row.IsEnabled);
        }
        finally { window.Close(); }
    }
}
