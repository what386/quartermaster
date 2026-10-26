using Quartermaster.Library.Storage;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Settings;

public static class StorageSize
{
    public static string Format(long bytes)
    {
        var value = (double)bytes;
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB" };
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return value.ToString(unit == 0 ? "0" : "0.##", Localizer.Current.Culture) + " " + units[unit];
    }
}

public sealed class StorageModRow(ModStorageUsage usage) : ViewModelBase
{
    public string Name => usage.Name;
    public string Version => usage.Version ?? Localizer.Text("Unknown version");
    public string Size => StorageSize.Format(usage.Bytes);
    public string Usage => (usage.ProfileCount > 0 ? Localizer.Current.Format(usage.ProfileCount == 1 ? "Used by {0} profile" : "Used by {0} profiles", usage.ProfileCount) : Localizer.Text("Not used by a profile")) +
        (usage.IsDeployed ? Localizer.Text(" · Deployed") : "") + (usage.IsPreviousVersion ? " · " + Localizer.Text("Previous version") : "");
}

public sealed class StorageCleanupRow(StorageCandidate candidate, Action changed) : ViewModelBase
{
    private bool selected;
    public StorageCandidate Candidate { get; } = candidate;
    public string Name => Candidate.Name;
    public string Size => StorageSize.Format(Candidate.Bytes);
    public string Reason => Localizer.Text(Candidate.Kind switch
    {
        StorageCleanupKind.PreviousVersion => "Previous version unused by profiles or deployment",
        StorageCleanupKind.OrphanedContent => "Unreferenced library content",
        _ => "Abandoned staging files older than one day"
    });
    public bool IsSelected { get => selected; set { if (Set(ref selected, value)) changed(); } }
}

public sealed partial class SettingsViewModel
{
    private StorageReport? storageReport;
    private Task? storageOverviewTask;
    private bool storageOverviewLoading;
    private string storageOverviewError = "";
    public bool IsStorageOverviewLoading => storageOverviewLoading;
    public string StorageOverviewError => storageOverviewError;
    public bool HasStorageOverviewError => storageOverviewError.Length > 0;
    public string StorageReclaimable => StorageSize.Format(storageReport?.ReclaimableBytes ?? 0);
    private string storageCleanupResult = "";
    public IReadOnlyList<StorageModRow> StorageMods { get; private set; } = [];
    public IReadOnlyList<StorageCleanupRow> StorageCandidates { get; private set; } = [];
    public bool HasStorageReport => storageReport is not null;
    public bool HasStorageCandidates => StorageCandidates.Count > 0;
    public bool IsStorageDeploymentUncertain => storageReport?.IsDeploymentUncertain == true;
    public string StorageTotal => StorageSize.Format(storageReport?.TotalBytes ?? 0);
    public string StorageLibrary => StorageSize.Format(storageReport?.LibraryBytes ?? 0);
    public string StorageTemporary => StorageSize.Format(storageReport?.TemporaryBytes ?? 0);
    public string StorageOther => StorageSize.Format(storageReport?.OtherBytes ?? 0);
    public string StorageDeployed => StorageSize.Format(storageReport?.DeployedBytes ?? 0);
    public double StorageLibraryPercent => Percent(storageReport?.LibraryBytes ?? 0);
    public double StorageTemporaryPercent => Percent(storageReport?.TemporaryBytes ?? 0);
    public double StorageOtherPercent => Percent(storageReport?.OtherBytes ?? 0);
    private double Percent(long bytes) => storageReport is { TotalBytes: > 0 } ? 100.0 * bytes / storageReport.TotalBytes : 0;
    public string StorageSelectionSummary
    {
        get
        {
            var count = StorageCandidates.Count(row => row.IsSelected);
            return Localizer.Current.Format(count == 1 ? "{0} item selected · {1}" : "{0} items selected · {1}", count,
                StorageSize.Format(StorageCandidates.Where(row => row.IsSelected).Sum(row => row.Candidate.Bytes)));
        }
    }
    public string StorageCleanupResult => storageCleanupResult;
    public AsyncCommand ScanStorageCommand { get; private set; } = null!;
    public AsyncCommand CleanStorageCommand { get; private set; } = null!;
    public AsyncCommand MoveDataDirectoryCommand { get; private set; } = null!;
    private void InitializeStorageCommands()
    {
        MoveDataDirectoryCommand = Operations.CreateCommand("Moving data folder", async ct =>
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("QUARTERMASTER_DATA_DIRECTORY")))
                throw new InvalidOperationException("The data folder is controlled by QUARTERMASTER_DATA_DIRECTORY.");
            var destination = await Services.Dialogs.PickFolderAsync(Localizer.Text("Choose an empty folder on the drive where Quartermaster should store its data"));
            if (destination is null) return;
            var message = Localizer.Current.Format("Move all Quartermaster data, including mods and profiles, to {0}? Quartermaster will restart when the move completes.", destination);
            if (!await Services.Dialogs.ConfirmAsync(Localizer.Text("Move data folder"), message, Localizer.Text("Move and restart"))) return;
            await Task.Run(() => DataDirectoryRelocator.Move(Services.DataDirectory, destination, AppServices.DataDirectoryPointer), ct);
            Services.RestartAfterDataMove();
        });
        ScanStorageCommand = Operations.CreateCommand("Scanning storage", async ct =>
        {
            storageCleanupResult = ""; Notify(nameof(StorageCleanupResult));
            await ScanStorageAsync(ct);
        });
        CleanStorageCommand = Operations.CreateCommand("Cleaning storage", async ct =>
        {
            var selected = StorageCandidates.Where(row => row.IsSelected).Select(row => row.Candidate).ToArray();
            var message = Localizer.Text("Permanently remove the selected files? Previous mod versions cannot be restored after cleanup.") + "\n\n" +
                string.Join("\n", selected.Select(item => $"• {item.Name} · {StorageSize.Format(item.Bytes)}"));
            if (!await Services.Dialogs.ConfirmAsync(Localizer.Text("Clean up storage"), message, Localizer.Text("Remove selected"))) return;
            StorageCleanupResult result;
            try { result = await Task.Run(() => Services.Storage.CleanupAsync(selected, ct), ct); }
            finally
            {
                // A failed file removal may still have committed metadata; reflect the actual library.
                await Session.ReloadAsync(CancellationToken.None);
                await ScanStorageAsync(CancellationToken.None);
            }
            storageCleanupResult = Localizer.Interpolate($"{Mods.ModPresentation.Count(result.RemovedItems, "item")} removed · {StorageSize.Format(result.RemovedBytes)} freed · {Mods.ModPresentation.Count(result.SkippedItems, "item")} changed or protected");
            Notify(nameof(StorageCleanupResult));
        }, () => StorageCandidates.Any(row => row.IsSelected));
    }
    private async Task ScanStorageAsync(CancellationToken ct)
    {
        if (storageOverviewTask is { IsCompleted: false }) await storageOverviewTask;
        ApplyStorageReport(await Task.Run(() => Services.Storage.ScanAsync(ct), ct));
    }
    public Task RefreshStorageOverviewAsync(CancellationToken ct = default)
    {
        if (storageOverviewTask is { IsCompleted: false }) return storageOverviewTask;
        return storageOverviewTask = LoadStorageOverviewAsync(ct);
    }
    private async Task LoadStorageOverviewAsync(CancellationToken ct)
    {
        storageOverviewLoading = true; Notify(nameof(IsStorageOverviewLoading));
        var state = Session.State;
        try { ApplyStorageReport(await Task.Run(() => Services.Storage.ReadOverviewAsync(state, ct), ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            storageOverviewError = Localizer.Text("Storage overview unavailable. Refresh to try again.");
            Notify(nameof(StorageOverviewError)); Notify(nameof(HasStorageOverviewError));
        }
        finally { storageOverviewLoading = false; Notify(nameof(IsStorageOverviewLoading)); }
    }
    private void ApplyStorageReport(StorageReport report)
    {
        storageReport = report;
        storageOverviewError = ""; Notify(nameof(StorageOverviewError)); Notify(nameof(HasStorageOverviewError));
        StorageMods = storageReport.Mods.Select(mod => new StorageModRow(mod)).ToArray();
        StorageCandidates = storageReport.Candidates.Select(candidate => new StorageCleanupRow(candidate, StorageSelectionChanged)).ToArray();
        foreach (var name in new[] { nameof(HasStorageReport), nameof(StorageMods), nameof(StorageCandidates), nameof(HasStorageCandidates), nameof(IsStorageDeploymentUncertain),
            nameof(StorageTotal), nameof(StorageLibrary), nameof(StorageTemporary), nameof(StorageOther), nameof(StorageDeployed), nameof(StorageReclaimable), nameof(StorageLibraryPercent),
            nameof(StorageTemporaryPercent), nameof(StorageOtherPercent) }) Notify(name);
        StorageSelectionChanged();
    }
    private void StorageSelectionChanged() { Notify(nameof(StorageSelectionSummary)); CleanStorageCommand.Refresh(); }
}
