using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public partial class ModDetailsDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    private bool attached;
    private ModDetailsViewModel? watched;
    public ModDetailsDialog()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => { attached = true; WatchDetails(); };
        DetachedFromVisualTree += (_, _) => { attached = false; WatchDetails(); };
        DataContextChanged += (_, _) => WatchDetails();
    }
    private void WatchDetails()
    {
        watched?.StopWatching();
        watched = attached ? DataContext as ModDetailsViewModel : null;
        watched?.StartWatching();
    }
    public bool TryAccept() => false;
    public void Cancel() => Completed?.Invoke(null);
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
