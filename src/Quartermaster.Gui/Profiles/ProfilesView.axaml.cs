using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Quartermaster.Gui.Profiles;

public partial class ProfilesView : UserControl
{
    private ModSettingsDialog? modDialog;
    public ProfilesView()
    {
        InitializeComponent();
        ProfileModsList.ContextMenu!.Opening += (_, _) =>
        {
            MoveToGroupMenuItem.Items.Clear();
            if (DataContext is not ProfilesViewModel { SelectedMod: { } selected } model) return;
            void AddTarget(string name, Guid? groupId) => MoveToGroupMenuItem.Items.Add(new MenuItem
            {
                Header = name,
                Command = new Shared.AsyncCommand(() => model.MoveModToGroupAsync(selected.Mod.Id, groupId),
                    () => model.Operations.CanInteract && model.Entries.Any(mod => mod.Mod.Id == selected.Mod.Id && mod.Entry.GroupId != groupId), model.Operations.ReportError)
            });
            AddTarget("Ungrouped", null);
            foreach (var group in model.Groups) AddTarget(group.Name, group.Id);
        };
        InitializeDrag();
    }
    private async void CreateGroupFromSelection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true, HasProfile: true } model) return;
        var ids = ProfileModsList.SelectedItems?.OfType<ProfileModItem>().Select(mod => mod.Mod.Id).ToArray() ?? [];
        ProfileModsList.ContextMenu?.Close();
        try { await model.CreateGroupFromModsAsync(ids); }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private void OpenModOptions(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (modDialog is not null || DataContext is not ProfilesViewModel { Operations.CanInteract: true } model ||
            e.Source is not Mods.ModRowView { DataContext: ProfileModItem { HasOptions: true } mod }) return;
        model.SelectedMod = mod;
        OpenModDetails(sender, e);
    }
    private void QueueModDetails(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source).OfType<ProfileGroupView>().Any()) return;
        if (!dragging && draggedItem is ProfileModItem) pendingModDetails = true;
    }
    private async void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source).OfType<ProfileGroupView>().Any()) return;
        if (modDialog is not null || DataContext is not ProfilesViewModel { HasSelectedMod: true } model ||
            !model.Operations.CanInteract || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        ProfileModsList.ContextMenu?.Close();
        modDialog = new() { DataContext = model };
        try { await owner.ShowDialogAsync<object?>(modDialog); }
        finally { modDialog = null; }
    }
}
