using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Shared;

public partial class AppUpdateDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public AppUpdateDialog() => InitializeComponent();
    public AppUpdateDialog(string version, string notes) : this()
    {
        TitleText.Text = Localizer.Interpolate($"Quartermaster {version} is available");
        NotesText.Text = notes;
        NotesText.IsVisible = !string.IsNullOrWhiteSpace(notes);
    }
    public bool TryAccept() { Completed?.Invoke(AppUpdateChoice.Update); return true; }
    public void Cancel() => Completed?.Invoke(AppUpdateChoice.Cancel);
    private void AcceptUpdate(object? sender, RoutedEventArgs e) => TryAccept();
    private void SkipUpdate(object? sender, RoutedEventArgs e) => Completed?.Invoke(AppUpdateChoice.Skip);
    private void CancelUpdate(object? sender, RoutedEventArgs e) => Cancel();
}
