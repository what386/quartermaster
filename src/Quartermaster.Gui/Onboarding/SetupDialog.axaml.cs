using Avalonia.Controls;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Onboarding;

public partial class SetupDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;

    public SetupDialog() => InitializeComponent();

    public SetupDialog(SetupViewModel model)
        : this()
    {
        DataContext = model;
        model.Completed += result => Completed?.Invoke(result);
    }

    public bool TryAccept()
    {
        if (DataContext is not SetupViewModel model || !model.NextCommand.CanExecute(null))
            return false;
        model.NextCommand.Execute(null);
        return true;
    }

    public void Cancel() => (DataContext as SetupViewModel)?.Cancel();
}
