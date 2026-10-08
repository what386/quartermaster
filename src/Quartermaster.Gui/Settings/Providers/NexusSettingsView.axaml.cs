using Avalonia.Controls;
namespace Quartermaster.Gui.Settings;

public partial class NexusSettingsView : UserControl
{
    public NexusSettingsView()
    {
        InitializeComponent();
        NexusApiKeyInput.GotFocus += (_, _) => NexusApiKeyInput.SelectAll();
    }
}
