using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;

namespace Quartermaster.Gui;

public partial class MainWindow
{
    private void ConfigureModDrops()
    {
        DragDrop.SetAllowDrop(ShellContent, true);
        void UpdateDragEffects(object? sender, DragEventArgs e)
        {
            var target = ResolveDropTarget(e);
            var paths = DroppedPaths(e);
            e.DragEffects = DataContext is MainWindowViewModel { Operations.CanInteract: true } &&
                !DialogOverlay.IsOpen && target.Accepted && paths.Count > 0 && (target.AcceptsMods || paths.All(path => !Directory.Exists(path)))
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        ShellContent.AddHandler(DragDrop.DragEnterEvent, UpdateDragEffects);
        ShellContent.AddHandler(DragDrop.DragOverEvent, UpdateDragEffects);
        ShellContent.AddHandler(DragDrop.DropEvent, ImportDroppedMods);
    }
    private (bool Accepted, Guid? ProfileId, bool Sidebar, bool AcceptsMods) ResolveDropTarget(DragEventArgs e)
    {
        // Native drag events target the allow-drop host; resolve the control under the cursor.
        if (this.InputHitTest(e.GetPosition(this)) is not Visual visual) return (false, null, false, false);
        foreach (var control in visual.GetVisualAncestors().Prepend(visual).OfType<Control>())
        {
            if (control is Button { DataContext: SidebarProfile profile }) return (true, profile.Profile.Id, true, true);
            if (control is Button { DataContext: NavigationItem { Page: PageKind.Mods } }) return (true, null, true, true);
            if (control is ModsView) return (true, null, false, true);
            if (control is ProfilesView { DataContext: ProfilesViewModel { SelectedProfile: { } selected } }) return (true, selected.Id, false, true);
            if (control == Sidebar) return (true, null, true, false);
        }
        return (false, null, false, false);
    }
    private static IReadOnlyList<string> DroppedPaths(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToArray();
        if (files is null || files.Length == 0) return [];
        var paths = files.Select(file => file.TryGetLocalPath()).ToArray();
        if (paths.Any(path => path is null || (!Directory.Exists(path) && !Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase)))) return [];
        return paths.OfType<string>().Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
    }
    private async void ImportDroppedMods(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel { Operations.CanInteract: true } model || DialogOverlay.IsOpen) return;
        var target = ResolveDropTarget(e);
        var paths = DroppedPaths(e);
        if (!target.Accepted || paths.Count == 0 || (!target.AcceptsMods && paths.Any(Directory.Exists))) { e.DragEffects = DragDropEffects.None; return; }
        e.Handled = true; e.DragEffects = DragDropEffects.Copy;
        try { await model.ImportDropsAsync(paths, target.ProfileId, target.Sidebar, target.AcceptsMods); }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
}
