using Avalonia.Controls;
using Avalonia.Input;
namespace Quartermaster.Gui.Search;
public partial class SearchView : UserControl
{
    public SearchView() => InitializeComponent();
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not SearchViewModel model || !model.SearchCommand.CanExecute(null)) return;
        e.Handled = true;
        model.SearchCommand.Execute(null);
    }
}
