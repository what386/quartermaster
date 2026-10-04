using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public enum ModImportKind { Link, Zip, Folder }
public sealed record ModImportRequest(ModImportKind Kind, string? Link = null);
public partial class AddModDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public AddModDialog() => InitializeComponent();
    private void LinkChanged(object? sender, TextChangedEventArgs e) => AcceptButton.IsEnabled = !string.IsNullOrWhiteSpace(LinkInput.Text);
    public bool TryAccept()
    {
        if (string.IsNullOrWhiteSpace(LinkInput.Text)) return false;
        var link = LinkInput.Text.Trim(); LinkInput.Text = "";
        Completed?.Invoke(new ModImportRequest(ModImportKind.Link, link)); return true;
    }
    public void Cancel() { LinkInput.Text = ""; Completed?.Invoke(null); }
    private void AcceptInput(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelInput(object? sender, RoutedEventArgs e) => Cancel();
    private void ChooseZip(object? sender, RoutedEventArgs e) => Completed?.Invoke(new ModImportRequest(ModImportKind.Zip));
    private void ChooseFolder(object? sender, RoutedEventArgs e) => Completed?.Invoke(new ModImportRequest(ModImportKind.Folder));
}
