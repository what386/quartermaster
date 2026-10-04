using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public partial class ModDetailsDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ModDetailsDialog() => InitializeComponent();
    public bool TryAccept() => false;
    public void Cancel() => Completed?.Invoke(null);
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
