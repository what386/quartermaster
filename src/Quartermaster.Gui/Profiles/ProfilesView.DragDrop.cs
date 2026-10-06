using System.Globalization;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Quartermaster.Gui.Profiles;

public partial class ProfilesView
{
    private ProfilesViewModel? observedModel;
    private IProfileListItem? draggedItem;
    private Guid? dragProfile;
    private HashSet<Guid> draggedModIds = [];
    private readonly Dictionary<(bool Group, Guid Id), double> dragOffsets = [];
    private bool collapseSelectionOnClick;
    private bool pendingModDetails;
    private Point dragStart;
    private double dragGrabOffset;
    private double dragBlockTop;
    private double dragBlockHeight;
    private TranslateTransform? dragTranslation;
    private IPointer? dragPointer;
    private ListBoxItem? dropTarget;
    private bool dragging;
    private bool dropAfter;
    private readonly HashSet<ListBoxItem> animatedRows = [];

    private void InitializeDrag()
    {
        ProfileModsList.AddHandler(PointerPressedEvent, StartDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerMovedEvent, UpdateDrag, RoutingStrategies.Tunnel);
        ProfileModsList.AddHandler(PointerReleasedEvent, FinishDrag, RoutingStrategies.Tunnel);
        ProfileModsList.PointerCaptureLost += (_, e) => { if (e.Pointer.Captured != ProfileModsList) ClearDrag(); };
        ProfileModsList.AddHandler(KeyDownEvent, (_, e) =>
        { if (e.Key == Key.Escape && draggedItem is not null) { ClearDrag(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => { ClearDrag(); ObserveModel(); };
        AttachedToVisualTree += (_, _) => ObserveModel();
        DetachedFromVisualTree += (_, _) => { ObserveModel(detached: true); ClearDrag(); };
    }
    private void ObserveModel(bool detached = false)
    {
        if (observedModel is not null) observedModel.PropertyChanged -= ModelChanged;
        observedModel = detached ? null : DataContext as ProfilesViewModel;
        if (observedModel is not null) observedModel.PropertyChanged += ModelChanged;
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (draggedItem is not null && e.PropertyName is nameof(ProfilesViewModel.SelectedProfile) or nameof(ProfilesViewModel.VisibleItems)) ClearDrag();
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
                if (ProfileModsList.SelectedItems?.Contains(clicked) != true) ProfileModsList.SelectedItem = clicked;
            }
            else if (row is null) ProfileModsList.SelectedItems?.Clear();
            return;
        }
        if (row?.DataContext is not IProfileListItem item || !properties.IsLeftButtonPressed) return;
        // The group header button remains clickable, but crossing the threshold turns it into a drag.
        if (item is ProfileModItem && ancestors.OfType<Button>().Any()) return;
        pendingModDetails = false;
        collapseSelectionOnClick = item is ProfileModItem && e.KeyModifiers == KeyModifiers.None &&
            ProfileModsList.SelectedItems?.Contains(item) == true && ProfileModsList.SelectedItems.Count > 1;
        if (collapseSelectionOnClick) { e.Handled = true; ProfileModsList.Focus(); }
        draggedItem = item; dragProfile = model.SelectedProfile?.Id;
        dragStart = e.GetPosition(ProfileModsList); dragPointer = e.Pointer;
    }
    private void UpdateDrag(object? sender, PointerEventArgs e)
    {
        if (draggedItem is null) return;
        if (DataContext is not ProfilesViewModel { Operations.CanInteract: true } model || model.SelectedProfile?.Id != dragProfile)
        { ClearDrag(); return; }
        var position = e.GetPosition(ProfileModsList);
        if (!dragging)
        {
            if (Math.Abs(position.X - dragStart.X) < 6 && Math.Abs(position.Y - dragStart.Y) < 6) return;
            dragging = true; pendingModDetails = false;
            if (draggedItem is ProfileModItem clicked)
            {
                var selected = ProfileModsList.SelectedItems?.OfType<ProfileModItem>().ToArray() ?? [];
                draggedModIds = (selected.Any(mod => mod.Mod.Id == clicked.Mod.Id) ? selected.Select(mod => mod.Mod.Id) : [clicked.Mod.Id]).ToHashSet();
                if (draggedModIds.Count == 1) model.SelectedListItem = draggedItem;
            }
            else model.SelectedListItem = draggedItem;
            e.Pointer.Capture(ProfileModsList);
            CreateDragVisual();
        }
        // Keep the horizontal position fixed and preserve where the pointer grabbed the row.
        dragTranslation!.Y = position.Y - dragGrabOffset;
        if (position.X < 0 || position.X > ProfileModsList.Bounds.Width || position.Y < 0 || position.Y > ProfileModsList.Bounds.Height)
        { SetDropTarget(null, false); e.Handled = true; return; }
        var scroll = ProfileModsList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is not null)
        {
            var delta = position.Y < 24 ? -16 : position.Y > ProfileModsList.Bounds.Height - 24 ? 16 : 0;
            if (delta != 0) scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y + delta, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
        }
        var rows = Rows().Where(row => draggedItem is not ProfileGroupItem || row.DataContext is ProfileGroupItem).ToArray();
        ListBoxItem? target = null; var after = false;
        foreach (var row in rows)
        {
            var top = LayoutTop(row);
            if (position.Y > top + row.Bounds.Height) continue;
            target = row; after = position.Y >= top + row.Bounds.Height / 2; break;
        }
        if (target is null && rows.LastOrDefault() is { } last) { target = last; after = true; }
        SetDropTarget(target, after);
        e.Handled = true;
    }
    private void CreateDragVisual()
    {
        var sources = Rows().Where(IsSource).ToArray();
        var first = sources[0];
        dragOffsets.Clear();
        dragBlockHeight = 0;
        foreach (var row in sources) { dragOffsets[RowKey(row)] = dragBlockHeight; dragBlockHeight += row.Bounds.Height; }
        var grabbed = sources.First(row => Equals(row.DataContext, draggedItem));
        dragBlockTop = LayoutTop(grabbed) - dragOffsets[RowKey(grabbed)];
        dragGrabOffset = dragStart.Y - dragBlockTop;
        DragVisuals.Children.Clear();
        foreach (var row in sources)
        {
            var presenter = row.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .FirstOrDefault(control => control.Name == "PART_ContentPresenter");
            DragVisuals.Children.Add(new Border
            {
                Background = presenter?.Background ?? row.Background,
                Height = row.Bounds.Height,
                Child = new ContentControl
                {
                    Content = row.DataContext,
                    HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                    VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch
                }
            });
        }
        DragPreview.Width = first.Bounds.Width;
        var left = first.GetVisualParent()?.TranslatePoint(first.Bounds.Position, ProfileModsList)?.X ?? 0;
        DragPreview.Margin = new Thickness(left, 0, 0, 0);
        dragTranslation = new TranslateTransform(0, dragBlockTop);
        DragPreview.RenderTransform = dragTranslation;
        DragPreview.IsVisible = true;
    }
    private ListBoxItem[] Rows() => ProfileModsList.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(row => row.DataContext is IProfileListItem && row.IsEffectivelyVisible).OrderBy(LayoutTop).ToArray();
    // Use layout coordinates so animated offsets cannot change which row is the drop target.
    private double LayoutTop(ListBoxItem row) => row.GetVisualParent()?.TranslatePoint(row.Bounds.Position, ProfileModsList)?.Y ?? 0;
    private static TransformOperations Translation(double x, double y) => TransformOperations.Parse(
        string.Create(CultureInfo.InvariantCulture, $"translate({x}px, {y}px)"));
    private bool IsSource(ListBoxItem row) => Equals(row.DataContext, draggedItem) || row.DataContext is ProfileModItem mod &&
        (draggedModIds.Contains(mod.Mod.Id) || draggedItem is ProfileGroupItem group && mod.Entry.GroupId == group.Id);
    private void SetDropTarget(ListBoxItem? row, bool after)
    {
        dropTarget?.Classes.Remove("dropBefore"); dropTarget?.Classes.Remove("dropAfter"); dropTarget?.Classes.Remove("dropInto");
        dropTarget = row; dropAfter = after;
        var intoGroup = draggedItem is ProfileModItem && row?.DataContext is ProfileGroupItem;
        row?.Classes.Add(intoGroup ? "dropInto" : after ? "dropAfter" : "dropBefore");
        var rows = Rows();
        var boundary = row is null ? -1 : Array.IndexOf(rows, row) + (after ? 1 : 0);
        // A mod dropped on a header joins the end of that group's visible block.
        if (row?.DataContext is ProfileGroupItem targetGroup && (after || intoGroup))
        {
            boundary = Array.IndexOf(rows, row) + 1;
            while (boundary < rows.Length && rows[boundary].DataContext is ProfileModItem mod && mod.Entry.GroupId == targetGroup.Id) boundary++;
        }
        var removedHeight = 0d;
        for (var i = 0; i < rows.Length; i++)
        {
            var current = rows[i]; animatedRows.Add(current);
            current.Classes.Set("dragging", dragging && IsSource(current));
            var offset = 0d;
            if (boundary >= 0 && !IsSource(current) && !IsSource(row!))
                offset = -removedHeight + (i >= boundary ? dragBlockHeight : 0);
            if (IsSource(current)) removedHeight += current.Bounds.Height;
            current.RenderTransform = Translation(0, offset);
        }
    }

    private async void FinishDrag(object? sender, PointerReleasedEventArgs e)
    {
        var source = draggedItem; var target = dropTarget?.DataContext as IProfileListItem;
        var profile = dragProfile; var after = dropAfter; var wasDragging = dragging;
        var selectedIds = draggedModIds.ToArray(); var collapseSelection = collapseSelectionOnClick; var openDetails = pendingModDetails;
        var positions = wasDragging ? Rows().ToDictionary(RowKey, row => IsSource(row)
            ? (dragTranslation?.Y ?? dragBlockTop) + dragOffsets.GetValueOrDefault(RowKey(row))
            : row.TranslatePoint(default, ProfileModsList)?.Y ?? LayoutTop(row)) : [];
        ClearDrag();
        if (!wasDragging)
        {
            if (collapseSelection && source is not null)
            { ProfileModsList.SelectedItems?.Clear(); ProfileModsList.SelectedItem = source; }
            if (openDetails) OpenModDetails(this, new RoutedEventArgs());
            return;
        }
        e.Handled = true;
        if (source is null || target is null || DataContext is not ProfilesViewModel model || model.SelectedProfile?.Id != profile) return;
        try
        {
            if (source is ProfileGroupItem group && target is ProfileGroupItem targetGroup) await model.MoveGroupAsync(group.Id, targetGroup.Id, after);
            else if (source is ProfileModItem mod)
            {
                if (target is ProfileGroupItem destination) await model.MoveModsToGroupAsync(selectedIds, destination.Id);
                else if (target is ProfileModItem destinationMod) await model.MoveModsAsync(selectedIds, destinationMod.Mod.Id, after);
            }
            if (!model.Operations.IsError)
            {
                if (source is ProfileModItem && model.SelectedProfile?.Id == profile)
                {
                    var selection = model.VisibleItems.OfType<ProfileModItem>().Where(mod => selectedIds.Contains(mod.Mod.Id)).ToArray();
                    if (ProfileModsList.SelectedItems is { } selected)
                    {
                        for (var index = selected.Count - 1; index >= 0; index--)
                            if (selected[index] is not ProfileModItem mod || !selection.Contains(mod)) selected.RemoveAt(index);
                        foreach (var mod in selection)
                            if (!selected.Contains(mod)) selected.Add(mod);
                    }
                }
                AnimateDrop(positions, profile);
            }
        }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private static (bool Group, Guid Id) RowKey(ListBoxItem row) => row.DataContext is ProfileGroupItem group
        ? (true, group.Id) : (false, ((ProfileModItem)row.DataContext!).Mod.Id);
    private void AnimateDrop(Dictionary<(bool Group, Guid Id), double> positions, Guid? profile)
    {
        // After the refreshed list lays out, slide surviving rows from their previous positions.
        Dispatcher.UIThread.Post(() =>
        {
            if (dragging || DataContext is not ProfilesViewModel model || model.SelectedProfile?.Id != profile || TopLevel.GetTopLevel(this) is null) return;
            ProfileModsList.UpdateLayout();
            foreach (var row in Rows())
            {
                if (!positions.TryGetValue(RowKey(row), out var oldTop)) continue;
                var offset = oldTop - LayoutTop(row);
                if (Math.Abs(offset) < 1) continue;
                var transitions = row.Transitions;
                row.Transitions = null;
                row.RenderTransform = Translation(0, offset);
                row.Transitions = transitions;
                row.RenderTransform = Translation(0, 0);
            }
        }, DispatcherPriority.Loaded);
    }
    private void ClearDrag()
    {
        var pointer = dragPointer;
        dropTarget?.Classes.Remove("dropBefore"); dropTarget?.Classes.Remove("dropAfter"); dropTarget?.Classes.Remove("dropInto");
        foreach (var row in animatedRows) { row.Classes.Remove("dragging"); row.RenderTransform = Translation(0, 0); }
        animatedRows.Clear(); DragPreview.IsVisible = false; DragVisuals.Children.Clear(); dragTranslation = null;
        draggedModIds.Clear(); dragOffsets.Clear(); collapseSelectionOnClick = false; pendingModDetails = false;
        draggedItem = null; dragProfile = null; dragPointer = null; dragging = false; dropTarget = null;
        if (pointer?.Captured == ProfileModsList) pointer.Capture(null);
    }
}
