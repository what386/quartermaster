using Avalonia.Controls;

namespace Quartermaster.Gui.Settings;

public partial class GitHubSettingsView : UserControl
{
    public GitHubSettingsView()
    {
        InitializeComponent();
        GitHubTokenInput.GotFocus += (_, _) => GitHubTokenInput.SelectAll();
    }
}
