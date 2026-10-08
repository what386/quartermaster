using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Shared;

public partial class ConfirmationDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ConfirmationDialog() => InitializeComponent();
    public ConfirmationDialog(string title, string message, string acceptLabel, string cancelLabel = "Cancel") : this()
    { TitleText.Text = title; MessageText.Text = message; AcceptButton.Content = Localizer.Text(acceptLabel); CancelButton.Content = Localizer.Text(cancelLabel); }
    public bool TryAccept() { Completed?.Invoke(true); return true; }
    public void Cancel() => Completed?.Invoke(false);
    private void AcceptConfirmation(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelConfirmation(object? sender, RoutedEventArgs e) => Cancel();
}
