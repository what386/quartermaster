using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Quartermaster.Gui.Mods;

public partial class ModRowView : UserControl
{
    public static readonly RoutedEvent<RoutedEventArgs> OptionsRequestedEvent =
        RoutedEvent.Register<ModRowView, RoutedEventArgs>(nameof(OptionsRequested), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> OptionsRequested
    {
        add => AddHandler(OptionsRequestedEvent, value);
        remove => RemoveHandler(OptionsRequestedEvent, value);
    }
    private void RequestOptions(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RaiseEvent(new RoutedEventArgs(OptionsRequestedEvent));
    }
    private Bitmap? icon;
    public ModRowView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => LoadIcon();
        AttachedToVisualTree += (_, _) => LoadIcon();
        DetachedFromVisualTree += (_, _) => ClearIcon();
    }
    private void ClearIcon()
    {
        ModIcon.Source = null;
        icon?.Dispose(); icon = null;
        ModMonogram.IsVisible = true;
    }
    private void LoadIcon()
    {
        ClearIcon();
        if (DataContext is not IModRow { IconPath: { } path }) return;
        try
        {
            using var input = File.OpenRead(path);
            icon = Bitmap.DecodeToWidth(input, 96);
            ModIcon.Source = icon;
            ModMonogram.IsVisible = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or NullReferenceException)
        { ClearIcon(); }
    }
}
