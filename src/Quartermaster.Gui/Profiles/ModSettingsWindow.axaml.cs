using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Quartermaster.Gui.Profiles;

public partial class ModSettingsWindow : Window
{
    public ModSettingsWindow() => InitializeComponent();
    private void CloseDialog(object? sender, RoutedEventArgs e) => Close();
}
