using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public partial class ModDetailsDialog : UserControl, IModalParentDialog
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
    public async void Cancel()
    {
        if (DataContext is ModDetailsViewModel details && !await details.SaveOnCloseAsync()) return;
        Completed?.Invoke(null);
    }
    private void CloseDialog(object? sender, RoutedEventArgs e) => Cancel();
}
