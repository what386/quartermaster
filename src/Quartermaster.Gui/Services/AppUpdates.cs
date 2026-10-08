using System.Reflection;
using Quartermaster.Gui.Shared;
using Quartermaster.SelfUpdate;

namespace Quartermaster.Gui.Services;

public sealed class AppUpdates : ViewModelBase, IDisposable
{
    private readonly AppServices services;
    private readonly Func<SelfUpdateManager> createManager;
    private readonly Action shutdown;
    private readonly CancellationTokenSource lifetime = new();
    private SelfUpdateManager? manager;
    private bool checking;
    public bool IsChecking { get => checking; private set => Set(ref checking, value); }
    public string Status { get; private set; } = "";
    public AppUpdates(AppServices services, Func<SelfUpdateManager>? createManager, Action shutdown)
    {
        this.services = services;
        this.shutdown = shutdown;
        this.createManager = createManager ?? (() => new(SelfUpdateOptions.ForCurrentApplication(
            typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? throw new InvalidOperationException(Localizer.Text("The application version is unavailable.")),
            Path.Combine(services.DataDirectory, "self-update"))));
    }
    private void SetStatus(string text) { Status = text; Notify(nameof(Status)); }

    public async Task CheckAtStartupAsync()
    {
        if (lifetime.IsCancellationRequested) return;
        try
        {
            manager ??= createManager();
            await manager.CleanupCompletedAsync(lifetime.Token);
            if (services.Session.Settings.AllowAutomaticUpdate) await CheckAsync(manual: false);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException)
        { } // Development launches do not have a portable installation to update.
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) services.Operations.ReportError(ex); }
    }

    public async Task CheckAsync(bool manual = true)
    {
        if (IsChecking || lifetime.IsCancellationRequested) return;
        IsChecking = true;
        SetStatus(Localizer.Text("Checking for app updates…"));
        try
        {
            manager ??= createManager();
            AppRelease? release = null;
            if (manual)
            {
                var completed = false;
                await services.Operations.RunAsync("Checking app updates", async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
                    release = await manager.CheckAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    completed = true;
                });
                if (services.Operations.IsError) { SetStatus(Localizer.Text("Update check failed.")); return; }
                if (!completed) { SetStatus(Localizer.Text("Update check cancelled.")); return; }
            }
            else release = await manager.CheckAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (release is null) { SetStatus(Localizer.Text("Quartermaster is up to date.")); return; }
            SetStatus(Localizer.Interpolate($"Quartermaster {release.Version} is available."));
            if (!manual && (!services.Session.Settings.AllowAutomaticUpdate ||
                release.Version == services.Session.Settings.SkippedAppUpdateVersion)) return;
            // Wait for imports, manual checks, and other operations before opening a prompt.
            while (services.Operations.IsBusy) await services.Operations.WhenIdle.WaitAsync(lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!manual && (!services.Session.Settings.AllowAutomaticUpdate ||
                release.Version == services.Session.Settings.SkippedAppUpdateVersion)) return;
            await services.Operations.RunAsync("App update", async ct =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
                var token = linked.Token;
                var choice = await services.Dialogs.PromptAppUpdateAsync(release.Version, release.Notes, token);
                token.ThrowIfCancellationRequested();
                if (choice == AppUpdateChoice.Skip)
                {
                    await services.Session.SkipAppUpdateAsync(release.Version, token);
                    SetStatus(Localizer.Interpolate($"Skipped {release.Version}."));
                    return;
                }
                if (choice != AppUpdateChoice.Update) return;
                var progress = services.Operations.CreateProgress<SelfUpdateProgress>(value => value.Phase switch
                {
                    SelfUpdatePhase.Downloading => Localizer.Interpolate($"Downloading app update · {value.Bytes / 1024 / 1024} MB"),
                    SelfUpdatePhase.Verifying => Localizer.Text("Verifying app update"),
                    SelfUpdatePhase.Extracting => Localizer.Text("Preparing app update"),
                    _ => Localizer.Text("Restarting Quartermaster"),
                });
                using var prepared = await manager.PrepareAsync(release, progress, token);
                manager.Start(prepared, token);
                shutdown();
            });
            if (services.Operations.IsError) SetStatus(Localizer.Text("App update failed."));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested) { SetStatus(Localizer.Text("Update check failed.")); services.Operations.ReportError(ex); }
        }
        finally { IsChecking = false; }
    }

    public void Dispose() { lifetime.Cancel(); manager?.Dispose(); }
}
