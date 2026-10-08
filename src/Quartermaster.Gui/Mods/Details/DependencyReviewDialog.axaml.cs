using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public partial class DependencyReviewDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public DependencyReviewDialog() => InitializeComponent();
    public DependencyReviewDialog(DependencyReviewViewModel model) : this() => DataContext = model;
    public bool TryAccept()
    {
        if (DataContext is not DependencyReviewViewModel { CanAccept: true }) return false;
        Completed?.Invoke(true); return true;
    }
    public void Cancel() => Completed?.Invoke(false);
    private void AcceptReview(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelReview(object? sender, RoutedEventArgs e) => Cancel();
}
