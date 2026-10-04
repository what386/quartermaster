using Avalonia.Threading;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Gui.Services;

/// <summary>Coordinates link imports, update actions and background downloads for all pages.</summary>
public sealed class ModDownloads : ViewModelBase
{
    private readonly AppServices services;
    private readonly Dictionary<Guid, DownloadRow> rows = [];
    public event EventHandler? Changed;
    public IReadOnlyList<DownloadRow> Jobs { get; private set; } = [];
    public ModDownloads(AppServices services)
    {
        this.services = services;
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
        Notify(nameof(Jobs)); Changed?.Invoke(this, EventArgs.Empty);
    }
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
        if (services.Providers.IsDownloadLink(link))
        { await services.Providers.HandleDownloadLinkAsync(link, ct, profileId); return; }
        var mod = await services.Providers.ResolveAsync(link, ct);
        var file = await services.Dialogs.ChooseModFileAsync(mod);
        if (file is null) return;
        ct.ThrowIfCancellationRequested();
        var job = await services.Providers.QueueAsync(file with { Name = mod.Name }, profileId, ct: ct);
        services.Providers.OpenDownloadPage(job.Id);
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
    }
    public UpdateCheck? AvailableUpdate(Mod mod) => services.Session.State.UpdateChecks.FirstOrDefault(check =>
        check.ModId == mod.Id && check.Error is null && check.AvailableFileId is not null &&
        mod.Sources.Any(source => source.Provider == check.Provider && source.FileId != check.AvailableFileId));
    public AsyncCommand? CreateUpdateCommand(Mod mod) => AvailableUpdate(mod) is null ? null :
        new AsyncCommand(() => services.Operations.RunAsync("Updating mod", async ct =>
        {
            var job = await services.Providers.QueueUpdateAsync(mod.Id, ct);
            services.Providers.OpenDownloadPage(job.Id);
        }), () => services.Operations.CanInteract && !services.Providers.State.Jobs.Any(job =>
            job.ReplacesModId == mod.Id && job.Status is DownloadStatus.Waiting or DownloadStatus.Downloading or DownloadStatus.Importing), services.Operations.ReportError);
    public string? UpdateDescription(Mod mod) => AvailableUpdate(mod) is { } check
        ? $"Update available{(check.AvailableVersion is null ? "" : " · " + check.AvailableVersion)}. Open the download page to upgrade." : null;
}

public sealed class DownloadRow : ViewModelBase
{
    private DownloadJob job;
    public string Name => job.File.Name + (job.File.Version is null ? "" : " · " + job.File.Version);
    public string Status => job.Status + (job.Error is null ? "" : " · " + job.Error);
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand RetryCommand { get; }
    public AsyncCommand CancelCommand { get; }
    public DownloadRow(DownloadJob job, AppServices services)
    {
        this.job = job;
        OpenCommand = services.Operations.CreateCommand("Opening download page", _ =>
        { services.Providers.OpenDownloadPage(job.Id); return Task.CompletedTask; });
        RetryCommand = services.Operations.CreateCommand("Retrying download", ct => services.Providers.RetryAsync(job.Id, ct),
            () => this.job.Status is DownloadStatus.Failed or DownloadStatus.Cancelled);
        CancelCommand = services.Operations.CreateCommand("Cancelling download", ct => services.Providers.CancelAsync(job.Id, ct),
            () => this.job.Status is DownloadStatus.Waiting or DownloadStatus.Downloading);
    }
    public void Update(DownloadJob value)
    { job = value; Notify(nameof(Name)); Notify(nameof(Status)); RetryCommand.Refresh(); CancelCommand.Refresh(); }
}
