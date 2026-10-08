using Avalonia.Controls;
using Avalonia.Media;
using System.ComponentModel;

namespace Quartermaster.Gui.Profiles;

public partial class ProfileGroupView : UserControl
{
    private ProfileGroupItem? model;
    public ProfileGroupView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Connect();
        AttachedToVisualTree += (_, _) => Connect();
        DetachedFromVisualTree += (_, _) => Disconnect();
    }
    private void Disconnect() { if (model is not null) model.PropertyChanged -= Changed; model = null; }
    private void Connect()
    {
        Disconnect(); model = DataContext as ProfileGroupItem;
        if (model is not null) model.PropertyChanged += Changed;
        UpdateColors();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(ProfileGroupItem.BackgroundColor) or nameof(ProfileGroupItem.TextColor)) UpdateColors(); }
    private void UpdateColors()
    {
        GroupButton.ClearValue(Button.BackgroundProperty); GroupButton.ClearValue(Button.ForegroundProperty);
        if (model?.BackgroundColor is { } background && Color.TryParse(background, out var color))
        {
            GroupButton.Background = new SolidColorBrush(color);
            // Keep labels readable when only a background color was chosen.
            GroupButton.Foreground = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 150 ? Brushes.Black : Brushes.White;
        }
        if (model?.TextColor is { } text && Color.TryParse(text, out var foreground)) GroupButton.Foreground = new SolidColorBrush(foreground);
    }
}
