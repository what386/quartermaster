using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Quartermaster.Gui.Mods;

/// <summary>Optional local artwork that releases its bitmap when a popup or option row is removed.</summary>
public sealed class ModPreviewImage : Image
{
    public static readonly StyledProperty<string?> FilePathProperty = AvaloniaProperty.Register<ModPreviewImage, string?>(nameof(FilePath));
    public string? FilePath { get => GetValue(FilePathProperty); set => SetValue(FilePathProperty, value); }
    public static readonly StyledProperty<bool> CanPreviewProperty = AvaloniaProperty.Register<ModPreviewImage, bool>(nameof(CanPreview), true);
    public bool CanPreview { get => GetValue(CanPreviewProperty); set => SetValue(CanPreviewProperty, value); }
    public static readonly StyledProperty<int> DecodeWidthProperty = AvaloniaProperty.Register<ModPreviewImage, int>(nameof(DecodeWidth), 320);
    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public static readonly RoutedEvent<RoutedEventArgs> PreviewRequestedEvent =
        RoutedEvent.Register<ModPreviewImage, RoutedEventArgs>(nameof(PreviewRequested), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> PreviewRequested
    {
        add => AddHandler(PreviewRequestedEvent, value);
        remove => RemoveHandler(PreviewRequestedEvent, value);
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (CanPreview && Source is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        { e.Handled = true; RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent)); }
        else base.OnPointerPressed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (CanPreview && Source is not null && e.Key is Key.Enter or Key.Space)
        { e.Handled = true; RaiseEvent(new RoutedEventArgs(PreviewRequestedEvent)); }
        else base.OnKeyDown(e);
    }
    private Bitmap? bitmap;
    public ModPreviewImage()
    {
        IsVisible = false; Focusable = true; Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, "Click to enlarge preview");
        AttachedToVisualTree += (_, _) => LoadImage();
        DetachedFromVisualTree += (_, _) => ClearImage();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FilePathProperty || change.Property == DecodeWidthProperty) LoadImage();
        if (change.Property == CanPreviewProperty)
        {
            Focusable = CanPreview; Cursor = CanPreview ? new Cursor(StandardCursorType.Hand) : null;
            ToolTip.SetTip(this, CanPreview ? "Click to enlarge preview" : null);
        }
    }
    private void ClearImage()
    {
        Source = null; bitmap?.Dispose(); bitmap = null; IsVisible = false;
    }
    private void LoadImage()
    {
        ClearImage();
        if (string.IsNullOrWhiteSpace(FilePath)) return;
        try
        {
            using var stream = File.OpenRead(FilePath);
            bitmap = DecodeWidth > 0 ? Bitmap.DecodeToWidth(stream, DecodeWidth) : new Bitmap(stream);
            Source = bitmap; IsVisible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException or NullReferenceException)
        { ClearImage(); }
    }
}
