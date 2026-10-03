using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public partial class ModSettingsDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ModSettingsDialog() => InitializeComponent();
    public bool TryAccept() => false;
    public void Cancel() => Completed?.Invoke(null);
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
