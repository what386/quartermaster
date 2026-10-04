namespace Quartermaster.Gui.Shared;

/// <summary>Serializes UI operations and reports errors without blocking the dispatcher.</summary>
public sealed class OperationState : ViewModelBase
{
    private bool busy;
    private bool showProgress;
    private string message = "Ready";
    private bool error;
    private string notificationMessage = "";
    private bool notificationVisible;
    private int notificationVersion;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? completion;
    public Task WhenIdle => completion?.Task ?? Task.CompletedTask;
    public bool IsBusy { get => busy; private set { if (Set(ref busy, value)) { Notify(nameof(CanInteract)); Notify(nameof(IsProgressVisible)); } } }
    public bool CanInteract => !IsBusy;
    public bool IsProgressVisible => IsBusy && showProgress;
    public string Message { get => message; private set => Set(ref message, value); }
    public bool IsError { get => error; private set => Set(ref error, value); }
    public string NotificationMessage { get => notificationMessage; private set => Set(ref notificationMessage, value); }
    public bool IsErrorNotificationVisible { get => notificationVisible; private set => Set(ref notificationVisible, value); }
    public int NotificationVersion => notificationVersion;
    public Command DismissErrorCommand { get; }
    public Command CancelCommand { get; }
    private readonly Func<string, string, string?, CancellationToken, Task>? log;
    public OperationState(Func<string, string, string?, CancellationToken, Task>? log = null)
    {
        this.log = log;
        CancelCommand = new(() => cancellation?.Cancel(), () => IsBusy);
        DismissErrorCommand = new(DismissErrorNotification);
    }

    public AsyncCommand CreateCommand(string label, Func<CancellationToken, Task> action, Func<bool>? allowed = null)
    {
        var command = new AsyncCommand(() => RunAsync(label, action), () => CanInteract && (allowed?.Invoke() ?? true), ReportError);
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) command.Refresh(); };
        return command;
    }

    public async Task RunAsync(string label, Func<CancellationToken, Task> action, bool showProgress = true)
    {
        if (IsBusy) return;
        using var source = new CancellationTokenSource();
        cancellation = source;
        var operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion = operationCompletion;
        this.showProgress = showProgress;
        IsError = false; Message = label; IsBusy = true; CancelCommand.Refresh();
        try
        {
            if (log is not null) await log(label, "started", null, CancellationToken.None);
            await action(source.Token); Message = label + " complete";
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { Message = "Operation cancelled"; }
        catch (Exception ex) { ReportError(ex); }
        finally
        {
            try { if (log is not null) await log(label, IsError ? "failed" : source.IsCancellationRequested ? "cancelled" : "complete", Message, CancellationToken.None); }
            finally { cancellation = null; IsBusy = false; CancelCommand.Refresh(); operationCompletion.TrySetResult(); }
        }
    }
    public void ReportError(Exception exception)
    {
        IsError = true; Message = exception.Message;
        ShowErrorNotification(exception.Message);
    }
    public void ShowErrorNotification(string text)
    {
        NotificationMessage = text;
        IsErrorNotificationVisible = true;
        notificationVersion++;
        Notify(nameof(NotificationVersion));
    }
    public void DismissErrorNotification() => IsErrorNotificationVisible = false;
    public IProgress<T> CreateProgress<T>(Func<T, string> format)
    {
        var operation = completion;
        return new Progress<T>(value =>
        {
            if (IsBusy && !IsError && operation == completion) Message = format(value);
        });
    }
}
