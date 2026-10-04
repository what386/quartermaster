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
        ProfileModsList.AddHandler(PointerPressedEvent, StartDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerMovedEvent, UpdateDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerReleasedEvent, FinishDrag, RoutingStrategies.Tunnel);
        ProfileModsList.PointerCaptureLost += (_, _) => ClearDrag();
        ProfileModsList.AddHandler(KeyDownEvent, (_, e) =>
        { if (e.Key == Key.Escape && draggedMod is not null) { ClearDrag(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        DetachedFromVisualTree += (_, _) => ClearDrag();
    }
    private void StartDrag(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true } model || e.Source is not Visual source) return;
        var ancestors = source.GetVisualAncestors().Prepend(source).ToArray();
        var row = ancestors.OfType<ListBoxItem>().FirstOrDefault();
        if (row?.DataContext is not ProfileModItem mod) return;
        var properties = e.GetCurrentPoint(ProfileModsList).Properties;
        if (properties.IsRightButtonPressed) { model.SelectedMod = mod; return; }
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
            .Where(row => row.DataContext is ProfileModItem && row.IsEffectivelyVisible)
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
        var target = dropTarget?.DataContext as ProfileModItem;
        var profile = dragProfile;
        var after = dropAfter;
        var wasDragging = dragging;
        ClearDrag();
        if (!wasDragging) return;
        e.Handled = true;
        if (source is null || target is null || DataContext is not ProfilesViewModel model || model.SelectedProfile?.Id != profile) return;
        try { await model.MoveModAsync(source.Mod.Id, target.Mod.Id, after); }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private void SetDropTarget(ListBoxItem? row, bool after)
    {
        dropTarget?.Classes.Remove("dropBefore"); dropTarget?.Classes.Remove("dropAfter");
        dropTarget = row; dropAfter = after;
        row?.Classes.Add(after ? "dropAfter" : "dropBefore");
    }
    private void ClearDrag()
    {
        var pointer = dragPointer;
        draggedMod = null; dragProfile = null; dragPointer = null; dragging = false;
        SetDropTarget(null, false);
        if (pointer?.Captured == ProfileModsList) pointer.Capture(null);
    }
    private async void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (modDialog is not null || DataContext is not ProfilesViewModel { HasSelectedMod: true } model ||
            !model.Operations.CanInteract || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        ProfileModsList.ContextMenu?.Close();
        modDialog = new() { DataContext = model };
        try { await owner.ShowDialogAsync<object?>(modDialog); }
        finally { modDialog = null; }
    }
}
