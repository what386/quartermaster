using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Quartermaster.Gui.Shared;

/// <summary>Loads public thumbnails without API credentials and releases them when the row leaves the page.</summary>
public sealed class RemoteImage : Image
{
    public static readonly StyledProperty<Uri?> SourceUriProperty = AvaloniaProperty.Register<RemoteImage, Uri?>(nameof(SourceUri));
    public Uri? SourceUri { get => GetValue(SourceUriProperty); set => SetValue(SourceUriProperty, value); }
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly HttpClient http;
    private CancellationTokenSource? loading;
    private Bitmap? bitmap;
    private bool attached;
    public RemoteImage() : this(client) { }
    public RemoteImage(HttpClient http)
    {
        this.http = http;
        AttachedToVisualTree += (_, _) => { attached = true; LoadImage(); };
        DetachedFromVisualTree += (_, _) => { attached = false; ClearImage(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceUriProperty && attached) LoadImage();
    }
    private void ClearImage()
    {
        loading?.Cancel(); loading?.Dispose(); loading = null;
        Source = null; bitmap?.Dispose(); bitmap = null;
    }
    private async void LoadImage()
    {
        ClearImage();
        if (SourceUri is not { IsAbsoluteUri: true, Scheme: "https", UserInfo: "" } uri) return;
        loading = new CancellationTokenSource();
        var ct = loading.Token;
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            const int maximum = 8 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) return;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920]; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + count > maximum) return;
                bytes.Write(buffer, 0, count);
            }
            bytes.Position = 0;
            var decoded = await Task.Run(() => Bitmap.DecodeToWidth(bytes, 240), ct);
            if (ct.IsCancellationRequested || !attached) { decoded.Dispose(); return; }
            bitmap = decoded; Source = bitmap;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        { /* Leave the placeholder visible when artwork is unavailable. */ }
    }
}
