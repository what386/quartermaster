using Avalonia.Controls;
using Avalonia.Input;

namespace Quartermaster.Gui;

public partial class MainWindow : Window
{
    public Task<T> ShowDialogAsync<T>(Control dialog) => DialogOverlay.ShowAsync<T>(dialog);
    public async Task WaitForDialogAsync(CancellationToken ct)
    {
        while (DialogOverlay.IsOpen) await DialogOverlay.WhenClosed.WaitAsync(ct);
    }
    private async void DeployProfile(object? sender, TappedEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarProfile profile } || DataContext is not MainWindowViewModel model) return;
        e.Handled = true;
        try { await model.DeployProfileAsync(profile.Profile.Id); }
        catch (Exception ex) { model.Operations.ReportError(ex); }
    }
    private bool waitingForOperation;
    public MainWindow()
    {
        InitializeComponent();
        ConfigureModDrops();
        ConfigureOnboarding();
        Closing += async (_, e) =>
        {
            DialogOverlay.CancelActiveDialog();
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
