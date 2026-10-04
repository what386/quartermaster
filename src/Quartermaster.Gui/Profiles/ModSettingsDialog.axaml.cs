using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public partial class ModSettingsDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    private Control? previewOrigin;
    public ModSettingsDialog()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => ImagePreview.Hide();
    }
    public bool TryAccept() => false;
    public void Cancel() { ImagePreview.Hide(); Completed?.Invoke(null); }
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
    private void OpenPreview(object? sender, ModImagePreviewEventArgs e)
    {
        e.Handled = true;
        previewOrigin = e.Origin;
        OptionsPanel.IsVisible = false;
        var size = TopLevel.GetTopLevel(this)?.Bounds.Size ?? new Size(1104, 824);
        Width = Math.Clamp(size.Width - 144, 520, 960); Height = Math.Clamp(size.Height - 144, 360, 680);
        ImagePreview.Show(e.FilePath, e.Title);
    }
    private void ClosePreview(object? sender, EventArgs e)
    {
        ImagePreview.Hide(); OptionsPanel.IsVisible = true; Width = 520; Height = 540;
        if (previewOrigin?.IsEffectivelyVisible == true) previewOrigin.Focus();
        else OptionsPanel.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Focusable)?.Focus();
        previewOrigin = null;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ImagePreview.IsVisible) { ClosePreview(this, EventArgs.Empty); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}
