using Avalonia.Threading;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Gui.Services;

/// <summary>Coordinates link imports, update actions and background downloads for all pages.</summary>
public sealed partial class ModDownloads : ViewModelBase
{
    private readonly AppServices services;
    private readonly Dictionary<Guid, DownloadRow> rows = [];
    public event EventHandler? Changed;
    public IReadOnlyList<DownloadRow> Jobs { get; private set; } = [];
    private string search = "";
    public string Search { get => search; set { if (Set(ref search, value)) { NotifyList(); Notify(nameof(VisibleManualChecks)); Notify(nameof(HasManualChecks)); } } }
    public IReadOnlyList<DownloadRow> VisibleJobs => Jobs.Where(row => row.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasVisibleJobs => VisibleJobs.Count > 0;
    public string EmptyMessage => Jobs.Count == 0 ? "No downloads queued." : "No downloads match your search.";
    public string CountLabel => ModPresentation.Count(Jobs.Count, "download");
    public OperationState Operations => services.Operations;
    public bool HasPendingDownloads => Jobs.Any(row => row.IsActive);
    public string PendingSummary
    {
        get
        {
            var waiting = Jobs.Count(row => row.IsWaiting);
            if (waiting > 0 && Jobs.Where(row => row.IsWaiting).All(row => row.IsManual))
                return $"Waiting for {ModPresentation.Count(waiting, "matching ZIP")}";
            return waiting > 0 ? $"Waiting for {ModPresentation.Count(waiting, "browser download")}" :
                $"Processing {ModPresentation.Count(Jobs.Count(row => row.IsActive), "download")}";
        }
    }
    public string PendingExplanation => Jobs.Any(row => row.IsWaiting && row.IsManual)
        ? "Watching your download folder for matching ZIPs."
        : Jobs.Any(row => row.IsWaiting)
        ? "Finish the download in your browser. Quartermaster watches your download folder and imports verified files automatically."
        : "Quartermaster is downloading or importing your mods. You can keep using the app.";
    public ModDownloads(AppServices services)
    {
        this.services = services;
        RestartManualChecksCommand = new(() => ShowManualChecks(), () => Operations.CanInteract);
        services.Session.Changed += (_, _) => RefreshManualChecks();
        services.Providers.DownloadFailed += job => Dispatcher.UIThread.Post(() =>
        {
            if (!services.IsDisposed) services.Operations.ShowErrorNotification($"Could not import {job.File.Name}: {job.Error}");
        });
        services.Providers.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        services.Providers.LibraryChanged += (_, _) => Dispatcher.UIThread.Post(async () =>
        {
            await services.Operations.WhenIdle;
            if (services.IsDisposed) return;
            try { await services.Session.ReloadAsync(CancellationToken.None); }
            catch (Exception ex) { services.Operations.ReportError(ex); }
        });
    }
    private void Refresh()
    {
        if (services.IsDisposed) return;
        Jobs = services.Providers.State.Jobs.Reverse().Select(job =>
        {
            if (!rows.TryGetValue(job.Id, out var row)) rows[job.Id] = row = new DownloadRow(job, services);
            row.Update(job); return row;
        }).ToArray();
        foreach (var id in rows.Keys.Where(id => !services.Providers.State.Jobs.Any(job => job.Id == id)).ToArray()) rows.Remove(id);
        Notify(nameof(Jobs)); NotifyList(); Notify(nameof(HasPendingDownloads)); Notify(nameof(PendingSummary)); Notify(nameof(PendingExplanation));
        RefreshManualChecks();
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void NotifyList()
    { Notify(nameof(VisibleJobs)); Notify(nameof(HasVisibleJobs)); Notify(nameof(EmptyMessage)); Notify(nameof(CountLabel)); }
    public async Task AddAsync(CancellationToken ct, Guid? profileId = null)
    {
        var request = await services.Dialogs.RequestModImportAsync();
        if (request is null) return;
        ct.ThrowIfCancellationRequested();
        if (request.Kind == ModImportKind.Link)
        { await AddLinkAsync(request.Link ?? "", ct, profileId); return; }
        var path = request.Kind == ModImportKind.Zip ? await services.Dialogs.PickModZipAsync() : await services.Dialogs.PickFolderAsync("Import mod folder");
        if (path is not null) await services.Session.ImportAsync(path, ct, profileId);
    }
    public async Task AddLinkAsync(string link, CancellationToken ct, Guid? profileId = null)
    {
        var direct = services.Providers.IsDownloadLink(link);
        var mod = await services.Providers.ResolveAsync(link, ct);
        var file = direct ? mod.Files.Single() : await services.Dialogs.ChooseModFileAsync(mod);
        if (file is null) return;
        // A manager link may be fulfilling an existing profile download.
        if (direct && profileId is null)
            profileId = services.Providers.State.Jobs.FirstOrDefault(job => job.File.Provider == file.Provider &&
                job.File.ModId == file.ModId && job.File.FileId == file.FileId &&
                job.Status is DownloadStatus.Waiting or DownloadStatus.Failed or DownloadStatus.Cancelled)?.ProfileId;
        var metadata = new Dictionary<string, IReadOnlyList<ModDependency>>();
        var dependencies = file.Provider == "nexusmods" ? await PlanNexusDependenciesAsync(mod, profileId, ct, metadata) : [];
        if (metadata.TryGetValue(file.ModId, out var rootDependencies)) file = file with { Dependencies = rootDependencies };
        var selectedFiles = new List<Quartermaster.Providers.Clients.ProviderFile>();
        foreach (var dependency in dependencies.Where(item => item.Installed is null))
        {
            var resolved = await services.Providers.ResolveAsync(dependency.Requirement.Page.AbsoluteUri, ct);
            var selected = await services.Dialogs.ChooseModFileAsync(resolved);
            if (selected is null) return;
            selectedFiles.Add(selected with { Name = resolved.Name, Dependencies = metadata[selected.ModId] });
        }
        ct.ThrowIfCancellationRequested();
        if (profileId is { } target && dependencies.Where(item => item.Installed is not null).Select(item => item.Installed!.Id).ToArray() is { Length: > 0 } ids)
            await services.Session.AddModsToProfileAsync(ids, target, ct);
        foreach (var selected in selectedFiles)
        {
            var queued = await services.Providers.QueueAsync(selected, profileId, ct: ct);
            if (!services.Providers.DownloadsDirectly(queued.File)) services.Providers.OpenDownloadPage(queued.Id);
        }
        if (direct) await services.Providers.HandleDownloadLinkAsync(link, ct, profileId, file.Dependencies);
        else
        {
            var job = await services.Providers.QueueAsync(file with { Name = mod.Name }, profileId, ct: ct);
            if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
        }
    }

    public async Task CheckUpdatesAsync(CancellationToken ct, IReadOnlyCollection<Guid>? modIds = null)
    {
        var started = DateTimeOffset.UtcNow;
        await services.Providers.CheckUpdatesAsync(modIds, ct);
        await services.Session.ReloadAsync(ct);
        var errors = services.Session.State.UpdateChecks.Where(check => check.CheckedAt >= started && check.Error is not null &&
            (modIds is null || modIds.Contains(check.ModId))).ToArray();
        if (errors.Length > 0) throw new InvalidOperationException($"Could not check {ModPresentation.Count(errors.Length, "mod")}: " +
            string.Join(" ", errors.Select(check => check.Error).Distinct().Take(3)));
        var updates = AvailableUpdates(modIds).Where(CanQueueUpdate).ToArray();
        Exception? applyError = null;
        if (updates.Length > 0 && await services.Dialogs.ConfirmAsync("Mod updates",
            $"Found {ModPresentation.Count(updates.Length, "update")}:\n" +
            string.Join("\n", updates.Select(mod => "• " + mod.Name +
                (AvailableUpdate(mod)?.AvailableVersion is { } version ? $" → {version}" : ""))) +
            "\n\nWould you like to apply them now?", "Update all", "Not now"))
        {
            try { await ApplyUpdatesAsync(ct, updates.Select(mod => mod.Id).ToArray()); }
            catch (Exception ex) when (ex is not OperationCanceledException) { applyError = ex; }
        }
        ct.ThrowIfCancellationRequested();
        var manualMods = services.Session.State.Mods.Where(mod => !mod.Superseded &&
            (modIds is null || modIds.Contains(mod.Id)) && !services.Providers.IsTracked(mod)).ToArray();
        if (manualMods.Length > 0 && await services.Dialogs.ConfirmAsync("Manual update checks",
            $"{ModPresentation.Count(manualMods.Length, "mod")} {(manualMods.Length == 1 ? "needs" : "need")} a manual update check. Open the checklist now?",
            "Open manual checks", "Not now"))
            ShowManualChecks(manualMods.Select(mod => mod.Id).ToArray());
        if (applyError is not null) throw applyError;
    }
    public IReadOnlyList<Mod> AvailableUpdates(IReadOnlyCollection<Guid>? modIds = null) => services.Session.State.Mods
        .Where(mod => !mod.Superseded && (modIds is null || modIds.Contains(mod.Id)) && AvailableUpdate(mod) is not null)
        .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    public bool CanQueueUpdate(Mod mod) => !services.Providers.State.Jobs.Any(job => job.ReplacesModId == mod.Id &&
        job.Status is DownloadStatus.Waiting or DownloadStatus.Downloading or DownloadStatus.Importing or DownloadStatus.NeedsConfirmation);
    public async Task ApplyUpdatesAsync(CancellationToken ct, IReadOnlyCollection<Guid>? modIds = null)
    {
        var failures = new List<string>();
        foreach (var mod in AvailableUpdates(modIds).Where(CanQueueUpdate).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var job = await services.Providers.QueueUpdateAsync(mod.Id, ct);
                if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add($"{mod.Name}: {ex.Message}"); }
        }
        if (failures.Count > 0) throw new InvalidOperationException("Could not start updates:\n" + string.Join("\n", failures));
    }
    public UpdateCheck? AvailableUpdate(Mod mod) => services.Session.State.UpdateChecks.FirstOrDefault(check =>
        check.ModId == mod.Id && check.Error is null && check.AvailableFileId is not null &&
        mod.Sources.Any(source => source.Provider == check.Provider && source.FileId != check.AvailableFileId));
    public AsyncCommand? CreateUpdateCommand(Mod mod) => AvailableUpdate(mod) is null ? null :
        new AsyncCommand(() => services.Operations.RunAsync("Updating mod", async ct =>
        {
            var job = await services.Providers.QueueUpdateAsync(mod.Id, ct);
            if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
        }), () => !services.Operations.IsProgressVisible && CanQueueUpdate(mod), services.Operations.ReportError);
    public string? UpdateDescription(Mod mod) => AvailableUpdate(mod) is { } check
        ? $"Update available{(check.AvailableVersion is null ? "" : " · " + check.AvailableVersion)}. Click Update to upgrade." : null;
}

public sealed class DownloadRow : ViewModelBase
{
    private DownloadJob job;
    private readonly bool downloadsDirectly;
    public string Name => job.File.Name + (job.File.Version is null ? "" : " · " + job.File.Version);
    public bool IsWaiting => job.Status == DownloadStatus.Waiting && !downloadsDirectly;
    public bool IsActive => job.Status is DownloadStatus.Waiting or DownloadStatus.Downloading or DownloadStatus.Importing;
    public string Status => (CanConfirm ? "Review update" : IsManual && IsWaiting ? "Waiting for a matching ZIP filename" : IsWaiting ? "Waiting for your browser download" : job.Status == DownloadStatus.Waiting ? "Queued" : job.Status.ToString()) + (job.Error is null ? "" : " · " + job.Error) + (job.Warning is null ? "" : " · " + job.Warning);
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public AsyncCommand AttachCommand { get; }
    public AsyncCommand ConfirmCommand { get; }
    public bool CanConfirm => job.Status == DownloadStatus.NeedsConfirmation;
    public AsyncCommand RemoveCommand { get; }
    public bool IsManual => job.File.Provider == "manual";
    public string OpenLabel => IsManual ? "Open mod page" : "Open download page";
    public bool CanAttach => job.Status is DownloadStatus.Waiting or DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.NeedsConfirmation;
    public bool CanCancel => job.Status is DownloadStatus.Waiting or DownloadStatus.Downloading or DownloadStatus.NeedsConfirmation;
    public bool CanRetry => job.Status is DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.NeedsConfirmation;
    public bool CanRemove => job.Status is DownloadStatus.Complete or DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.NeedsConfirmation;
    public DownloadRow(DownloadJob job, AppServices services)
    {
        this.job = job;
        downloadsDirectly = services.Providers.DownloadsDirectly(job.File);
        OpenCommand = services.Operations.CreateCommand("Opening download page", _ =>
        { services.Providers.OpenDownloadPage(job.Id); return Task.CompletedTask; });
        RetryCommand = services.Operations.CreateCommand("Retrying download", ct => services.Providers.RetryAsync(job.Id, ct),
            () => CanRetry);
        ConfirmCommand = services.Operations.CreateCommand("Importing mod update", async ct =>
        {
            await services.Providers.ConfirmManualUpdateAsync(job.Id, ct);
            await services.Session.ReloadAsync(ct);
        }, () => CanConfirm);
        AttachCommand = services.Operations.CreateCommand("Attaching mod ZIP", async ct =>
        {
            var path = await services.Dialogs.PickModZipAsync();
            if (path is null) return;
            await services.Providers.AttachZipAsync(job.Id, path, ct);
            await services.Session.ReloadAsync(ct);
        }, () => CanAttach);
        CancelCommand = services.Operations.CreateCommand("Cancelling download", ct => services.Providers.RemoveAsync(job.Id, ct), () => CanCancel);
        RemoveCommand = services.Operations.CreateCommand("Removing download", ct => services.Providers.RemoveAsync(job.Id, ct), () => CanRemove);
    }
    public void Update(DownloadJob value)
    {
        job = value; Notify(nameof(Name)); Notify(nameof(Status)); Notify(nameof(IsWaiting)); Notify(nameof(IsActive));
        Notify(nameof(CanAttach)); Notify(nameof(CanCancel)); Notify(nameof(CanRetry)); Notify(nameof(CanRemove));
        Notify(nameof(CanConfirm)); ConfirmCommand.Refresh();
        RetryCommand.Refresh(); CancelCommand.Refresh(); AttachCommand.Refresh(); RemoveCommand.Refresh();
    }
}
