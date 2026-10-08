using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Shared;

public sealed record GroupColors(string? Background, string? Text);
public partial class GroupColorsDialog : UserControl, IModalDialog
{
    public event Action<object?>? Completed;
    private bool syncingPickers;
    public GroupColorsDialog()
    {
        InitializeComponent();
        BackgroundPicker.PropertyChanged += (_, e) =>
        { if (!syncingPickers && e.Property == ColorPicker.ColorProperty) BackgroundInput.Text = Services.ThemeManager.Format(BackgroundPicker.Color); };
        ForegroundPicker.PropertyChanged += (_, e) =>
        { if (!syncingPickers && e.Property == ColorPicker.ColorProperty) ForegroundInput.Text = Services.ThemeManager.Format(ForegroundPicker.Color); };
        UpdatePreview();
    }
    public GroupColorsDialog(string? background, string? text) : this()
    { BackgroundInput.Text = background; ForegroundInput.Text = text; UpdatePreview(); }
    private bool Validate(out GroupColors? colors)
    {
        try
        {
            colors = new(ProfileAppearance.NormalizeColor(BackgroundInput.Text), ProfileAppearance.NormalizeColor(ForegroundInput.Text));
            return true;
        }
        catch (ArgumentException) { colors = null; return false; }
    }
    private void ColorChanged(object? sender, TextChangedEventArgs e) { if (SaveButton is not null) UpdatePreview(); }
    private void UpdatePreview()
    {
        var valid = Validate(out var colors);
        SaveButton.IsEnabled = valid; ErrorText.IsVisible = !valid;
        if (!valid) return;
        Preview.Background = colors!.Background is { } background ? Brush.Parse(background) : (IBrush?)Avalonia.Application.Current?.FindResource("AccentBrush");
        PreviewText.Foreground = colors.Text is { } text ? Brush.Parse(text) : (IBrush?)Avalonia.Application.Current?.FindResource("AccentForegroundBrush");
        if (colors.Text is null && colors.Background is { } value)
        {
            var color = Color.Parse(value);
            PreviewText.Foreground = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) > 150 ? Brushes.Black : Brushes.White;
        }
        syncingPickers = true;
        try
        {
            if (Preview.Background is ISolidColorBrush backgroundBrush) BackgroundPicker.Color = backgroundBrush.Color;
            if (PreviewText.Foreground is ISolidColorBrush textBrush) ForegroundPicker.Color = textBrush.Color;
        }
        finally { syncingPickers = false; }
    }
    public bool TryAccept()
    {
        if (!Validate(out var colors)) return false;
        Completed?.Invoke(colors); return true;
    }
    public void Cancel() => Completed?.Invoke(null);
    private void SaveColors(object? sender, RoutedEventArgs e) => TryAccept();
    private void CancelColors(object? sender, RoutedEventArgs e) => Cancel();
    private void ResetColors(object? sender, RoutedEventArgs e) { BackgroundInput.Text = ""; ForegroundInput.Text = ""; }
}
