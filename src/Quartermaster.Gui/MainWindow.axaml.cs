using Avalonia.Controls;

namespace Quartermaster.Gui;

public partial class MainWindow : Window
{
    private bool waitingForOperation;
    public MainWindow()
    {
        InitializeComponent();
        Closing += async (_, e) =>
        {
            if (DataContext is not MainWindowViewModel { Operations.IsBusy: true } viewModel) return;
            e.Cancel = true;
            if (waitingForOperation) return;
            waitingForOperation = true;
            viewModel.Operations.CancelCommand.Execute(null);
            await viewModel.Operations.WhenIdle;
            waitingForOperation = false;
            Close();
        };
    }
}
