using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Quartermaster.Gui.Mods;

public partial class ModImagePreview : UserControl
{
    public event EventHandler? BackRequested;
    public ModImagePreview() => InitializeComponent();
    public void Show(string path, string title)
    {
        PreviewTitle.Text = title; FullImage.FilePath = path; IsVisible = true;
        // The first opening needs a layout pass before the button can receive keyboard focus.
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible) BackButton.Focus();
        }, DispatcherPriority.Loaded);
    }
    public void Hide()
    {
        FullImage.FilePath = null; IsVisible = false;
    }
    private void ReturnToOptions(object? sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
