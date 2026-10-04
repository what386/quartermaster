using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;

namespace Quartermaster.Gui.Shared;

/// <summary>A small activity ring that runs only while attached and visible.</summary>
public sealed class BusySpinner : UserControl
{
    private readonly DispatcherTimer timer;
    private readonly RotateTransform rotation = new();
    private bool attached;
    public BusySpinner()
    {
        Width = 24; Height = 24;
        var arc = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse("M 12,2 A 10,10 0 1 1 2,12"), StrokeThickness = 2.5,
            Stretch = Stretch.Uniform, RenderTransform = rotation,
            RenderTransformOrigin = RelativePoint.Center
        };
        arc.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Binding(nameof(Foreground)) { Source = this });
        Content = arc;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => rotation.Angle = (rotation.Angle + 12) % 360;
        AttachedToVisualTree += (_, _) => { attached = true; UpdateTimer(); };
        DetachedFromVisualTree += (_, _) => { attached = false; timer.Stop(); };
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && timer is not null) UpdateTimer();
    }
    private void UpdateTimer() { if (attached && IsVisible) timer.Start(); else timer.Stop(); }
}
