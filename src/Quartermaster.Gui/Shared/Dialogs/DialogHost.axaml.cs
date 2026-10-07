using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Quartermaster.Gui.Shared;

public interface IModalDialog
{
    event Action<object?>? Completed;
    bool TryAccept();
    void Cancel();
}

/// <summary>Hosts one modal inside the shell and restores focus when it closes.</summary>
public partial class DialogHost : UserControl
{
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<DialogHost, bool>(nameof(IsOpen));
    public bool IsOpen { get => GetValue(IsOpenProperty); private set => SetValue(IsOpenProperty, value); }
    private IModalDialog? active;
    private TaskCompletionSource? closed;
    public Task WhenClosed => closed?.Task ?? Task.CompletedTask;
    public DialogHost() { InitializeComponent(); IsVisible = false; }
    public Task<T> ShowAsync<T>(Control content)
    {
        if (active is not null) throw new InvalidOperationException("Close the current dialog first.");
        if (content is not IModalDialog dialog) throw new ArgumentException("Content must be a modal dialog.", nameof(content));
        var previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        closed = dialogClosed;
        void Finish(object? result)
        {
            dialog.Completed -= Finish;
            active = null; DialogContent.Content = null; IsOpen = false; IsVisible = false;
            if (previousFocus?.IsEffectivelyEnabled == true && previousFocus.IsEffectivelyVisible) previousFocus.Focus();
            completion.TrySetResult(result is T value ? value : default!);
            dialogClosed.TrySetResult();
        }
        dialog.Completed += Finish;
        active = dialog; DialogContent.Content = content; IsOpen = true; IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (active != dialog) return;
            var focus = content.GetVisualDescendants().OfType<Control>().FirstOrDefault(c =>
                c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            focus?.Focus();
        });
        return completion.Task;
    }
    public void CancelActiveDialog() => active?.Cancel();
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && active is not null) { active.Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter && active?.TryAccept() == true) e.Handled = true;
        else base.OnKeyDown(e);
    }
}
