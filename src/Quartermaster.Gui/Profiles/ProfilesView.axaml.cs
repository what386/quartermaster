using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Profiles;

public partial class ProfilesView : UserControl
{
    private ModSettingsDialog? modDialog;
    public ProfilesView() => InitializeComponent();
    private async void OpenModDetails(object? sender, RoutedEventArgs e)
    {
        if (modDialog is not null || DataContext is not ProfilesViewModel { HasSelectedMod: true } model ||
            !model.Operations.CanInteract || TopLevel.GetTopLevel(this) is not MainWindow owner) return;
        modDialog = new() { DataContext = model };
        try { await owner.ShowDialogAsync<object?>(modDialog); }
        finally { modDialog = null; }
    }
}
