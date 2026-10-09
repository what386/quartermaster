using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public partial class ConflictInspectorDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public ConflictInspectorDialog() => InitializeComponent();
    public bool TryAccept() => false;
    public void Cancel() => Completed?.Invoke(null);
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
