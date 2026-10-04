using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace Quartermaster.Gui.Shared;

/// <summary>Draws the app's path-only SVG icons using the current foreground color.</summary>
public sealed class SvgIcon : Control
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<SvgIcon, string?>(nameof(Source));
    public static readonly StyledProperty<IBrush> ForegroundProperty = AvaloniaProperty.Register<SvgIcon, IBrush>(nameof(Foreground), Brushes.White);
    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public IBrush Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    private sealed record IconPath(Geometry Geometry, double Width, PenLineCap Cap, PenLineJoin Join);
    private sealed record Icon(Rect ViewBox, IconPath[] Paths);
    private static readonly Dictionary<string, Icon> Cache = [];
    private Icon? icon;
    static SvgIcon() => AffectsRender<SvgIcon>(SourceProperty, ForegroundProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SourceProperty) return;
        icon = null;
        if (Source is null) return;
        if (!Cache.TryGetValue(Source, out icon))
        {
            using var stream = AssetLoader.Open(new Uri(Source));
            var root = XDocument.Load(stream).Root!;
            var bounds = root.Attribute("viewBox")!.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
            var paths = root.Elements(root.Name.Namespace + "path").Select(path => new IconPath(
                Geometry.Parse(path.Attribute("d")!.Value),
                double.Parse(path.Attribute("stroke-width")?.Value ?? "1", CultureInfo.InvariantCulture),
                path.Attribute("stroke-linecap")?.Value == "round" ? PenLineCap.Round : PenLineCap.Flat,
                path.Attribute("stroke-linejoin")?.Value == "round" ? PenLineJoin.Round : PenLineJoin.Miter)).ToArray();
            icon = new(new Rect(bounds[0], bounds[1], bounds[2], bounds[3]), paths);
            Cache.Add(Source, icon);
        }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (icon is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var box = icon.ViewBox;
        var scale = Math.Min(Bounds.Width / box.Width, Bounds.Height / box.Height);
        var transform = Matrix.CreateTranslation(-box.X, -box.Y) * Matrix.CreateScale(scale, scale) *
            Matrix.CreateTranslation((Bounds.Width - box.Width * scale) / 2, (Bounds.Height - box.Height * scale) / 2);
        using (context.PushTransform(transform))
            foreach (var path in icon.Paths)
                context.DrawGeometry(null, new Pen(Foreground, path.Width, lineCap: path.Cap, lineJoin: path.Join), path.Geometry);
    }
}
