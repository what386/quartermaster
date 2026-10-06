using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Mods;

namespace Quartermaster.Gui.Mods;

public sealed record LocalModImportOptions(string? PageLink = null);

public partial class ModImportDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ModImportDialog() => InitializeComponent();
    public ModImportDialog(string source) : this() => SourceText.Text = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
    private void PageChanged(object? sender, TextChangedEventArgs e)
    {
        if (AcceptButton is null || ValidationText is null) return;
        AcceptButton.IsEnabled = ValidatePage(out _);
    }
    private bool ValidatePage(out string? page)
    {
        try
        {
            page = ModLinks.ValidatePage(PageInput.Text);
            ValidationText.IsVisible = false;
            return true;
        }
        catch (ArgumentException ex)
        {
            page = null; ValidationText.Text = ex.Message; ValidationText.IsVisible = true;
            return false;
        }
    }
    public bool TryAccept()
    {
        if (!ValidatePage(out var page)) return false;
        Completed?.Invoke(new LocalModImportOptions(page)); return true;
    }
    public void Cancel() => Completed?.Invoke(null);
    private void AcceptImport(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelImport(object? sender, RoutedEventArgs e) => Cancel();
}
