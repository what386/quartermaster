using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Gui.Mods;

public partial class ModFilesDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ModFilesDialog() => InitializeComponent();
    public ModFilesDialog(ProviderMod mod) : this()
    {
        DataContext = mod;
        FileChoices.SelectedItem = mod.Files.FirstOrDefault(file => file.IsPrimary && file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            ?? mod.Files.FirstOrDefault(file => file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ?? mod.Files.FirstOrDefault();
    }
    private void FileChanged(object? sender, SelectionChangedEventArgs e) => AcceptButton.IsEnabled = FileChoices.SelectedItem is ProviderFile;
    public bool TryAccept()
    {
        if (FileChoices.SelectedItem is not ProviderFile file) return false;
        Completed?.Invoke(file); return true;
    }
    public void Cancel() => Completed?.Invoke(null);
    private void AcceptInput(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelInput(object? sender, RoutedEventArgs e) => Cancel();
}
