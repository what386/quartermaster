using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Quartermaster.Gui.Shared;

public partial class ErrorNotification : UserControl
{
    private readonly DispatcherTimer timer = new();
    private OperationState? state;
    private bool attached;
    public TimeSpan DismissAfter { get; set; } = TimeSpan.FromSeconds(7);

    public ErrorNotification()
    {
        InitializeComponent();
        timer.Tick += (_, _) => { timer.Stop(); state?.DismissErrorNotification(); };
        DataContextChanged += (_, _) => BindState();
        AttachedToVisualTree += (_, _) => { attached = true; BindState(); };
        DetachedFromVisualTree += (_, _) => { attached = false; BindState(); };
    }

    private void BindState()
    {
        timer.Stop();
        if (state is not null) state.PropertyChanged -= StateChanged;
        state = attached ? DataContext as OperationState : null;
        if (state is not null) state.PropertyChanged += StateChanged;
        RestartTimer();
    }

    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationState.NotificationVersion) or nameof(OperationState.IsErrorNotificationVisible)) RestartTimer();
    }

    private void RestartTimer()
    {
        timer.Stop();
        if (state?.IsErrorNotificationVisible == true) { timer.Interval = DismissAfter; timer.Start(); }
    }

    private void Dismiss(object? sender, PointerPressedEventArgs e)
    {
        state?.DismissErrorNotification();
        e.Handled = true;
    }
}
