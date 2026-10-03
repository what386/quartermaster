using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Shared;

public partial class TextInputDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public TextInputDialog() => InitializeComponent();
    public TextInputDialog(string title, string prompt, string acceptLabel, string? initialValue = null) : this()
    { TitleText.Text = title; PromptText.Text = prompt; AcceptButton.Content = acceptLabel; NameInput.Text = initialValue; }
    private void NameChanged(object? sender, TextChangedEventArgs e) =>
        AcceptButton.IsEnabled = !string.IsNullOrWhiteSpace(NameInput.Text);
    public bool TryAccept()
    {
        if (string.IsNullOrWhiteSpace(NameInput.Text)) return false;
        Completed?.Invoke(NameInput.Text.Trim()); return true;
    }
    public void Cancel() => Completed?.Invoke(null);
    private void AcceptInput(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelInput(object? sender, RoutedEventArgs e) => Cancel();
}
