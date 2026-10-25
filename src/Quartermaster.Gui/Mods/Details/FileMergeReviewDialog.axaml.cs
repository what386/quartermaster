using Avalonia.Controls;
using Avalonia.Interactivity;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public partial class FileMergeReviewDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    public FileMergeReviewDialog() => InitializeComponent();
    public FileMergeReviewDialog(FileMergeReviewViewModel model) : this() => DataContext = model;
    public bool TryAccept()
    {
        if (DataContext is not FileMergeReviewViewModel) return false;
        Completed?.Invoke(true); return true;
    }
    public void Cancel() => Completed?.Invoke(false);
    private void AcceptReview(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelReview(object? sender, RoutedEventArgs e) => Cancel();
}
