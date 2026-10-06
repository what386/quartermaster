using Quartermaster.Gui.Shared;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Gui.Services;

public sealed partial class ModDownloads
{
    private IReadOnlyCollection<Guid>? manualScope;
    private readonly HashSet<Guid> reviewed = [];
    private readonly HashSet<Guid> completedManualJobs = [];
    public Command RestartManualChecksCommand { get; }
    public IReadOnlyList<ManualCheckRow> ManualChecks { get; private set; } = [];
    public IReadOnlyList<ManualCheckRow> VisibleManualChecks => ManualChecks.Where(row => row.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public bool HasManualChecks => VisibleManualChecks.Count > 0;
    public string ManualCount => $"{ManualChecks.Count} mods to check";
    public event EventHandler? ManualChecksRequested;
    public void ShowManualChecks(IReadOnlyCollection<Guid>? ids = null)
    {
        manualScope = ids ?? services.Session.State.Mods.Select(mod => mod.Id).ToArray();
        completedManualJobs.UnionWith(services.Providers.State.Jobs.Where(job => job.File.Provider == "manual" &&
            job.Status == DownloadStatus.Complete).Select(job => job.Id));
        reviewed.Clear(); Search = "";
        RefreshManualChecks();
        ManualChecksRequested?.Invoke(this, EventArgs.Empty);
    }
    private void RefreshManualChecks()
    {
        foreach (var job in services.Providers.State.Jobs.Where(job => job.File.Provider == "manual" &&
            job.Status == DownloadStatus.Complete && job.ReplacesModId is not null))
            if (completedManualJobs.Add(job.Id)) reviewed.Add(job.ReplacesModId!.Value);
        ManualChecks = services.Session.State.Mods.Where(mod => !mod.Superseded && !services.Providers.IsTracked(mod) && !reviewed.Contains(mod.Id)
            && (manualScope is null || manualScope.Contains(mod.Id)))
            .OrderBy(mod => mod.Name, StringComparer.OrdinalIgnoreCase).Select(mod => new ManualCheckRow(mod, services, () =>
            { reviewed.Add(mod.Id); RefreshManualChecks(); })).ToArray();
        Notify(nameof(ManualChecks)); Notify(nameof(VisibleManualChecks)); Notify(nameof(HasManualChecks)); Notify(nameof(ManualCount));
    }
}

public sealed class ManualCheckRow
{
    private readonly Mod mod;
    private readonly AppServices services;
    public string Name => mod.Name;
    public string PageLink => ModLinks.PageFor(mod) ?? "No page link set";
    public string Filename => mod.ImportedFileName ?? mod.Name + ".zip";
    private DownloadJob? ConfirmationJob => services.Providers.State.Jobs.FirstOrDefault(job =>
        job.ReplacesModId == mod.Id && job.Status == DownloadStatus.NeedsConfirmation);
    public string? Warning => ConfirmationJob?.Warning;
    public bool CanConfirm => ConfirmationJob is not null;
    public AsyncCommand ConfirmCommand { get; }
    public AsyncCommand SetPageCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand AttachCommand { get; }
    public AsyncCommand DoneCommand { get; }
    public ManualCheckRow(Mod mod, AppServices services, Action done)
    {
        this.mod = mod; this.services = services;
        ConfirmCommand = services.Operations.CreateCommand("Importing mod update", async ct =>
        {
            if (ConfirmationJob is not { } job) return;
            await services.Providers.ConfirmManualUpdateAsync(job.Id, ct);
            await services.Session.ReloadAsync(ct); done();
        }, () => CanConfirm);
        SetPageCommand = services.Operations.CreateCommand("Setting mod page", async ct =>
        {
            var link = await services.Dialogs.RequestTextAsync("Mod page", "Page link (HTTPS)", "Save", ModLinks.PageFor(mod));
            if (link is null) return;
            await services.Library.SetPageLinkAsync(mod.Id, link, ct);
            await services.Session.ReloadAsync(ct);
        });
        OpenCommand = services.Operations.CreateCommand("Checking mod manually", async ct =>
        {
            var job = await QueueAsync(ct);
            if (job is not null) services.Providers.OpenDownloadPage(job.Id);
        });
        AttachCommand = services.Operations.CreateCommand("Attaching mod update", async ct =>
        {
            var path = await services.Dialogs.PickModZipAsync();
            if (path is null) return;
            var job = await QueueAsync(ct);
            if (job is null) return;
            await services.Providers.AttachZipAsync(job.Id, path, ct);
            await services.Session.ReloadAsync(ct); done();
        });
        DoneCommand = services.Operations.CreateCommand("Finishing manual check", async ct =>
        {
            foreach (var job in services.Providers.State.Jobs.Where(job => job.File.Provider == "manual" && job.ReplacesModId == mod.Id
                && job.Status is DownloadStatus.Waiting or DownloadStatus.Failed or DownloadStatus.Cancelled or DownloadStatus.NeedsConfirmation).ToArray())
                await services.Providers.RemoveAsync(job.Id, ct);
            done();
        });
    }
    private async Task<DownloadJob?> QueueAsync(CancellationToken ct)
    {
        var current = services.Session.State.Mods.Single(item => item.Id == mod.Id);
        if (ModLinks.PageFor(current) is null)
        {
            var link = await services.Dialogs.RequestTextAsync("Mod page", "Page link (HTTPS)", "Save");
            if (link is null) return null;
            await services.Library.SetPageLinkAsync(mod.Id, link, ct);
            await services.Session.ReloadAsync(ct);
        }
        return await services.Providers.QueueManualUpdateAsync(mod.Id, ct);
    }
}
