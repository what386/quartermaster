using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Quartermaster.Gui.Mods;

public sealed class ModImagePreviewEventArgs(RoutedEvent routedEvent, string path, string title, Control origin) : RoutedEventArgs(routedEvent)
{
    public string FilePath { get; } = path;
    public string Title { get; } = title;
    public Control Origin { get; } = origin;
}

public partial class ModOptionsView : UserControl
{
    public static readonly RoutedEvent<ModImagePreviewEventArgs> PreviewRequestedEvent =
        RoutedEvent.Register<ModOptionsView, ModImagePreviewEventArgs>(nameof(PreviewRequested), RoutingStrategies.Bubble);
    public event EventHandler<ModImagePreviewEventArgs> PreviewRequested
    {
        add => AddHandler(PreviewRequestedEvent, value);
        remove => RemoveHandler(PreviewRequestedEvent, value);
    }
    public ModOptionsView() => InitializeComponent();
    private void RequestPreview(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not ModPreviewImage { Source: not null, FilePath: not null } image) return;
        var path = image.FilePath;
        var title = image.DataContext switch
        {
            OptionEdit option => option.HasChoices ? $"{option.Name} · {option.Choices[option.ChoiceIndex].Name}" : option.Name,
            OptionChoiceItem choice => choice.Name,
            _ => Localizer.Text("Image preview")
        };
        // Popup visuals route separately; relay the captured preview from the options view.
        foreach (var combo in this.GetVisualDescendants().OfType<ComboBox>()) combo.IsDropDownOpen = false;
        RaiseEvent(new ModImagePreviewEventArgs(PreviewRequestedEvent, path, title, image));
    }
}
