using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Settings;

public partial class StorageDialog : UserControl, IModalParentDialog
{
    public event Action<object?>? Completed;
    public StorageDialog() => InitializeComponent();
    public bool TryAccept() => false;
    public void Cancel() { if (DataContext is SettingsViewModel { Operations.CanInteract: true }) Completed?.Invoke(null); }
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
