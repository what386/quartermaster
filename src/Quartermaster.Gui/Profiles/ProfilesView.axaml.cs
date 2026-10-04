using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Quartermaster.Gui.Profiles;

public partial class ProfilesView : UserControl
{
    private ModSettingsDialog? modDialog;
    private ProfileModItem? draggedMod;
    private Guid? dragProfile;
    private Point dragStart;
    private IPointer? dragPointer;
    private ListBoxItem? dropTarget;
    private bool dragging;
    private bool dropAfter;
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
        ProfileModsList.AddHandler(PointerPressedEvent, StartDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerMovedEvent, UpdateDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerReleasedEvent, FinishDrag, RoutingStrategies.Tunnel);
        ProfileModsList.PointerCaptureLost += (_, _) => ClearDrag();
        ProfileModsList.AddHandler(KeyDownEvent, (_, e) =>
        { if (e.Key == Key.Escape && draggedMod is not null) { ClearDrag(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => ClearDrag();
    }
    private async void CreateGroupFromSelection(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true, HasProfile: true } model) return;
        var ids = ProfileModsList.SelectedItems?.OfType<ProfileModItem>().Select(mod => mod.Mod.Id).ToArray() ?? [];
        ProfileModsList.ContextMenu?.Close();
        try { await model.CreateGroupFromModsAsync(ids); }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private void StartDrag(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true } model || e.Source is not Visual source) return;
        var ancestors = source.GetVisualAncestors().Prepend(source).ToArray();
        var row = ancestors.OfType<ListBoxItem>().FirstOrDefault();
        var properties = e.GetCurrentPoint(ProfileModsList).Properties;
        if (properties.IsRightButtonPressed)
        {
            if (row?.DataContext is ProfileModItem clicked)
            {
                if (ProfileModsList.SelectedItems?.Contains(clicked) != true)
                    ProfileModsList.SelectedItem = clicked;
            }
            else if (row is null) ProfileModsList.SelectedItems?.Clear();
            return;
        }
        if (row?.DataContext is not ProfileModItem mod) return;
        if (!properties.IsLeftButtonPressed || ancestors.OfType<Button>().Any()) return;
        draggedMod = mod; dragProfile = model.SelectedProfile?.Id;
        dragStart = e.GetPosition(ProfileModsList); dragPointer = e.Pointer;
    }
    private void UpdateDrag(object? sender, PointerEventArgs e)
    {
        if (draggedMod is null) return;
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true } model || model.SelectedProfile?.Id != dragProfile)
        { ClearDrag(); return; }
        var position = e.GetPosition(ProfileModsList);
        if (!dragging)
        {
            if (Math.Abs(position.X - dragStart.X) < 6 && Math.Abs(position.Y - dragStart.Y) < 6) return;
            dragging = true; model.SelectedMod = draggedMod;
            e.Pointer.Capture(ProfileModsList);
        }
        SetDropTarget(null, false);
        if (position.X < 0 || position.X > ProfileModsList.Bounds.Width || position.Y < 0 || position.Y > ProfileModsList.Bounds.Height) return;
        var scroll = ProfileModsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null)
        {
            var delta = position.Y < 24 ? -16 : position.Y > ProfileModsList.Bounds.Height - 24 ? 16 : 0;
            if (delta != 0) scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        var rows = ProfileModsList.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(row => row.DataContext is IProfileListItem && row.IsEffectivelyVisible)
            .OrderBy(row => row.TranslatePoint(new Point(0, 0), ProfileModsList)?.Y).ToArray();
        foreach (var row in rows)
        {
            var top = row.TranslatePoint(new Point(0, 0), ProfileModsList)?.Y;
            if (top is null || position.Y > top + row.Bounds.Height) continue;
            SetDropTarget(row, position.Y >= top + row.Bounds.Height / 2); break;
        }
        if (dropTarget is null && rows.LastOrDefault() is { } last) SetDropTarget(last, true);
        e.Handled = true;
    }
    private async void FinishDrag(object? sender, PointerReleasedEventArgs e)
    {
        var source = draggedMod;
        var target = dropTarget?.DataContext as IProfileListItem;
        var profile = dragProfile;
        var after = dropAfter;
        var wasDragging = dragging;
        ClearDrag();
        if (!wasDragging) return;
        e.Handled = true;
        if (source is null || target is null || DataContext is not ProfilesViewModel model || model.SelectedProfile?.Id != profile) return;
        try
        {
            if (target is ProfileGroupItem group) await model.MoveModToGroupAsync(source.Mod.Id, group.Id);
            else if (target is ProfileModItem mod) await model.MoveModAsync(source.Mod.Id, mod.Mod.Id, after);
        }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private void SetDropTarget(ListBoxItem? row, bool after)
    {
        dropTarget?.Classes.Remove("dropBefore"); dropTarget?.Classes.Remove("dropAfter"); dropTarget?.Classes.Remove("dropInto");
        dropTarget = row; dropAfter = after;
        row?.Classes.Add(row.DataContext is ProfileGroupItem ? "dropInto" : after ? "dropAfter" : "dropBefore");
    }
    private void ClearDrag()
    {
        var pointer = dragPointer;
        draggedMod = null; dragProfile = null; dragPointer = null; dragging = false;
        SetDropTarget(null, false);
        if (pointer?.Captured == ProfileModsList) pointer.Capture(null);
    }
    private void OpenModOptions(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (modDialog is not null || DataContext is not ProfilesViewModel { Operations.CanInteract: true } model ||
            e.Source is not Mods.ModRowView { DataContext: ProfileModItem { HasOptions: true } mod }) return;
        model.SelectedMod = mod;
        OpenModDetails(sender, e);
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
