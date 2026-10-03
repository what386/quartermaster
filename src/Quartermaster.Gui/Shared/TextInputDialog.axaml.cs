using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Shared;

public partial class TextInputDialog : Window
{
    public TextInputDialog()
    {
        InitializeComponent();
        Opened += (_, _) => NameInput.Focus();
    }
    public TextInputDialog(string title, string prompt, string acceptLabel) : this()
    { Title = title; PromptText.Text = prompt; AcceptButton.Content = acceptLabel; }
    private void NameChanged(object? sender, TextChangedEventArgs e) =>
        AcceptButton.IsEnabled = !string.IsNullOrWhiteSpace(NameInput.Text);
    private void AcceptInput(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(NameInput.Text)) Close(NameInput.Text.Trim());
    }
    private void CancelInput(object? sender, RoutedEventArgs e) => Close(null);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(null); e.Handled = true; }
        else if (e.Key == Key.Enter && AcceptButton.IsEnabled) { Close(NameInput.Text!.Trim()); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}
