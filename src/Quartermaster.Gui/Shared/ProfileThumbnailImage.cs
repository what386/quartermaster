using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Quartermaster.Gui.Shared;

/// <summary>Shows embedded profile artwork and releases it when the sidebar item is removed.</summary>
public sealed class ProfileThumbnailImage : Image
{
    public static readonly StyledProperty<string?> ThumbnailProperty = AvaloniaProperty.Register<ProfileThumbnailImage, string?>(nameof(Thumbnail));
    public string? Thumbnail { get => GetValue(ThumbnailProperty); set => SetValue(ThumbnailProperty, value); }
    private Bitmap? bitmap;
    public ProfileThumbnailImage()
    {
        AttachedToVisualTree += (_, _) => Load();
        DetachedFromVisualTree += (_, _) => Clear();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == ThumbnailProperty) Load(); }
    private void Clear() { Source = null; bitmap?.Dispose(); bitmap = null; IsVisible = false; }
    private void Load()
    {
        Clear();
        if (Thumbnail is null) return;
        try
        {
            Library.Profiles.ProfileAppearance.ValidateThumbnail(Thumbnail);
            using var input = new MemoryStream(Convert.FromBase64String(Thumbnail));
            bitmap = Bitmap.DecodeToWidth(input, 96);
            Source = bitmap; IsVisible = true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or NotSupportedException)
        { Clear(); }
    }
}
