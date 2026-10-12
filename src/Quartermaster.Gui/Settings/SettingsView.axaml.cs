using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Settings;

public partial class SettingsView : UserControl
{
    private bool storageOpen;
    private CancellationTokenSource? overviewCancellation;
    public SettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += async (_, _) =>
        {
            overviewCancellation = new();
            if (DataContext is SettingsViewModel model) await model.RefreshStorageOverviewAsync(overviewCancellation.Token);
        };
        DetachedFromVisualTree += (_, _) => { overviewCancellation?.Cancel(); overviewCancellation?.Dispose(); overviewCancellation = null; };
    }
    private async void OpenStorage(object? sender, RoutedEventArgs e)
    {
        if (storageOpen || DataContext is not SettingsViewModel { Operations.CanInteract: true } model || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        storageOpen = true;
        try
        {
            await model.ScanStorageCommand.ExecuteAsync();
            await owner.ShowDialogAsync<object?>(new StorageDialog { DataContext = model });
        }
        catch (Exception ex) { model.Operations.ReportError(ex); }
        finally { storageOpen = false; }
    }
}
