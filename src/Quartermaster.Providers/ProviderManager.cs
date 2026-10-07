using Quartermaster.Library.Mods;
using Quartermaster.Providers.Clients;
using Quartermaster.Providers.Clients.NexusMods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers;

/// <summary>Persists browser-download requests and imports verified archives without changing browser files.</summary>
public sealed class ProviderManager(
    LibraryService library,
    DownloadStore store,
    IEnumerable<IModProvider> providers,
    string cacheDirectory,
    Action<Uri>? openBrowser = null
) : IAsyncDisposable
{
    private readonly Dictionary<string, IModProvider> providers = providers.Append(new ManualDownloads()).ToDictionary(p => p.Id);
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim imports = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, (CancellationTokenSource Cancellation, Task Task)> workers =
    [];
    public DownloadState State { get; private set; } = new([], []);
    public event EventHandler? Changed;
    public event EventHandler? LibraryChanged;
    public event Action<DownloadJob>? DownloadFailed;
    public IReadOnlyList<IModProvider> AvailableProviders => providers.Values.Where(p => p.Id != ManualDownloads.ProviderId).ToArray();
    public bool IsTracked(Mod mod) => mod.Sources.Any(source => providers.TryGetValue(source.Provider, out var provider) && provider.SupportsUpdateChecks);

    public async Task<DownloadJob> QueueManualUpdateAsync(Guid modId, CancellationToken ct = default)
    {
        var mod = (await library.LoadAsync(ct)).Mods.Single(mod => mod.Id == modId);
        if (IsTracked(mod)) throw new InvalidOperationException("This mod has provider tracking. Use Check updates instead.");
        var page = ModLinks.PageFor(mod) ?? throw new InvalidOperationException("Set this mod's page link before checking it manually.");
        var file = new ProviderFile(ManualDownloads.ProviderId, modId.ToString(), "manual-update", mod.Name,
            mod.ImportedFileName ?? mod.Name + ".zip", null, new Uri(page))
        { PageLink = new Uri(page) };
        return await QueueAsync(file, replacesModId: modId, ct: ct, installedAsDependency: mod.InstalledAsDependency);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            State = await store.LoadAsync(ct);
            State = State with
            {
                Jobs = State
                    .Jobs.Select(job =>
                        job.Status is DownloadStatus.Downloading or DownloadStatus.Importing
                            ? job with
                            {
                                Status = DownloadStatus.Waiting,
                                Error = null,
                            }
                            : job
                    )
                    .ToArray(),
            };
            await store.SaveAsync(State, ct);
            foreach (var job in State.Jobs.Where(j => j.Status == DownloadStatus.Waiting))
                Start(job);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IModProvider FindProvider(string link)
    {
        if (
            !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length != 0
            || !uri.IsDefaultPort
        )
            throw new ArgumentException("Enter a valid mod link.");
        return providers.Values.SingleOrDefault(provider => provider.CanHandle(uri))
            ?? throw new NotSupportedException("No installed provider supports this mod link.");
    }

    public bool IsDownloadLink(string link) =>
        FindProvider(link).IsDownloadLink(new Uri(link.Trim()));

    public bool DownloadsDirectly(ProviderFile file) =>
        GetProvider(file.Provider).DownloadsDirectly;

    public Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default) =>
        FindProvider(link).ResolveAsync(link.Trim(), ct);
    public Task<IReadOnlyList<ModRequirement>> GetRequirementsAsync(string link, CancellationToken ct = default) =>
        FindProvider(link).GetRequirementsAsync(link, ct);

    public Task<ProviderMod> ResolveAsync(
        string provider,
        string link,
        CancellationToken ct = default
    ) => GetProvider(provider).ResolveAsync(link, ct);

    public Task<IReadOnlyList<SearchResult>> SearchAsync(
        string provider,
        string query,
        CancellationToken ct = default
    ) => GetProvider(provider).SearchAsync(query, ct: ct);

    /// <summary>Opens the exact queued file's public download page in the default browser.</summary>
    public void OpenDownloadPage(Guid jobId) =>
        OpenDownloadPage(State.Jobs.Single(job => job.Id == jobId).File);

    public void OpenDownloadPage(ProviderFile file) =>
        GetProvider(file.Provider).CreateScanner().OpenDownloadPage(file, openBrowser);

    public async Task SetDirectoriesAsync(
        IReadOnlyList<string> directories,
        CancellationToken ct = default
    )
    {
        var folders = directories.Select(Path.GetFullPath).Distinct().ToArray();
        if (folders.Length == 0)
            throw new ArgumentException("Choose at least one download folder.");
        await MutateAsync(state => state with { Directories = folders }, ct);
    }

    public async Task<DownloadJob> QueueAsync(
        ProviderFile file,
        Guid? profileId = null,
        Guid? replacesModId = null,
        CancellationToken ct = default,
        bool installedAsDependency = false,
        IReadOnlyList<Guid>? additionalProfileIds = null
    )
    {
        GetProvider(file.Provider);
        if (
            !file.DownloadPage.IsAbsoluteUri
            || file.DownloadPage.Scheme != "https"
            || file.DownloadPage.UserInfo.Length != 0
        )
            throw new ArgumentException("Download pages must be public HTTPS links.");
        if (file.Provider == NexusAdapter.ProviderId)
        {
            var page = NexusLink.Parse(file.DownloadPage.AbsoluteUri);
            if (page.ModId.ToString() != file.ModId || page.FileId?.ToString() != file.FileId)
                throw new ArgumentException("Download page belongs to another Nexus file.");
            file = file with { DownloadPage = page.Page };
        }
        if (!file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Only ZIP mods can currently be imported. Choose a ZIP file from the provider."
            );
        await gate.WaitAsync(ct);
        DownloadJob job;
        try
        {
            job =
                State.Jobs.FirstOrDefault(j =>
                    j.File.Provider == file.Provider
                    && j.File.ModId == file.ModId
                    && j.File.FileId == file.FileId
                    && j.ProfileId == profileId
                    && j.ReplacesModId == replacesModId
                    && j.Status
                        is DownloadStatus.Waiting
                            or DownloadStatus.Downloading
                            or DownloadStatus.Importing
                            or DownloadStatus.NeedsConfirmation
                ) ?? new(Guid.NewGuid(), file, ProfileId: profileId, ReplacesModId: replacesModId)
                { InstalledAsDependency = installedAsDependency, AdditionalProfileIds = additionalProfileIds ?? [] };
            if (!State.Jobs.Any(j => j.Id == job.Id))
            {
                if (file.Provider == ManualDownloads.ProviderId)
                    job = job with { ExistingFiles = ManualDownloads.Snapshot(State.Directories) };
                var updated = State with { Jobs = [.. State.Jobs, job] };
                await store.SaveAsync(updated, ct);
                State = updated;
                Start(job);
            }
            else
            {
                job = job with
                {
                    InstalledAsDependency = job.InstalledAsDependency && installedAsDependency,
                    AdditionalProfileIds = job.AdditionalProfileIds.Concat(additionalProfileIds ?? []).Distinct().ToArray()
                };
                var updated = State with { Jobs = State.Jobs.Select(item => item.Id == job.Id ? job : item).ToArray() };
                await store.SaveAsync(updated, ct);
                State = updated;
            }
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return job;
    }

    public Task HandleNxmAsync(
        string link,
        CancellationToken ct = default,
        Guid? profileId = null
    ) => HandleDownloadLinkAsync(link, ct, profileId);

    public async Task HandleDownloadLinkAsync(
        string link,
        CancellationToken ct = default,
        Guid? profileId = null,
        IReadOnlyList<ModDependency>? dependencies = null
    )
    {
        var provider = FindProvider(link);
        if (!provider.IsDownloadLink(new Uri(link.Trim())))
            throw new ArgumentException("Expected a direct mod-manager download link.");
        var mod = await provider.ResolveAsync(link.Trim(), ct);
        var file = mod.Files.Single() with { Dependencies = dependencies };
        var existing = State.Jobs.FirstOrDefault(j =>
            j.File.Provider == file.Provider
            && j.File.ModId == file.ModId
            && j.File.FileId == file.FileId
            && (profileId is null || j.ProfileId == profileId)
            && j.Status
                is DownloadStatus.Waiting
                    or DownloadStatus.Failed
                    or DownloadStatus.Cancelled
        );
        var job = existing ?? await QueueAsync(file, profileId, ct: ct);
        await StopWorkerAsync(job.Id);
        if (dependencies is not null)
            await MutateAsync(state => state with { Jobs = state.Jobs.Select(item => item.Id == job.Id
                ? item with { File = item.File with { Dependencies = dependencies } } : item).ToArray() }, ct);
        await SetStatusAsync(job.Id, DownloadStatus.Waiting);
        await gate.WaitAsync(ct);
        try
        {
            Start(State.Jobs.Single(j => j.Id == job.Id), link.Trim());
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CancelAsync(Guid id, CancellationToken ct = default)
    {
        await StopWorkerAsync(id);
        await SetStatusAsync(id, DownloadStatus.Cancelled, ct: ct);
    }

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        await StopWorkerAsync(id);
        await MutateAsync(
            state => state with { Jobs = state.Jobs.Where(job => job.Id != id).ToArray() },
            ct
        );
    }

    /// <summary>The user explicitly associates an existing ZIP with this request, bypassing automatic provider recognition.</summary>
    public async Task AttachZipAsync(Guid id, string archive, CancellationToken ct = default)
    {
        archive = Path.GetFullPath(archive);
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !File.Exists(archive))
            throw new ArgumentException("Choose an existing ZIP archive.");
        await StopWorkerAsync(id, preserveImporting: true);
        Task task;
        await gate.WaitAsync(ct);
        try
        {
            var job = State.Jobs.Single(j => j.Id == id);
            if (job.Status is DownloadStatus.Complete or DownloadStatus.Importing)
                throw new InvalidOperationException(
                    "This download is already complete or being imported."
                );
            Start(job, archive: archive, ct: ct);
            task = workers[id].Task;
        }
        finally
        {
            gate.Release();
        }
        await task;
        if (
            State.Jobs.Single(j => j.Id == id) is
            { Status: DownloadStatus.Failed, Error: { } error }
        )
            throw new InvalidDataException(error);
        ct.ThrowIfCancellationRequested();
    }

    public async Task RetryAsync(Guid id, CancellationToken ct = default)
    {
        await StopWorkerAsync(id);
        await SetStatusAsync(id, DownloadStatus.Waiting, ct: ct);
        await gate.WaitAsync(ct);
        try
        {
            Start(State.Jobs.Single(j => j.Id == id));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ConfirmManualUpdateAsync(Guid id, CancellationToken ct = default)
    {
        var job = State.Jobs.Single(job => job.Id == id);
        if (job.File.Provider != ManualDownloads.ProviderId || job.Status != DownloadStatus.NeedsConfirmation || job.ConfirmationFile is not { } candidate)
            throw new InvalidOperationException("This download is not awaiting confirmation.");
        var file = new FileInfo(candidate.Path);
        if (!file.Exists || file.Length != candidate.Size || file.LastWriteTimeUtc != candidate.LastWrite)
            throw new InvalidOperationException("The ZIP changed after the warning. Retry the download check before importing.");
        await AttachZipAsync(id, candidate.Path, ct);
    }

    public Task CheckUpdatesAsync(CancellationToken ct = default) => CheckUpdatesAsync(null, ct);

    public async Task CheckUpdatesAsync(
        IReadOnlyCollection<Guid>? modIds,
        CancellationToken ct = default
    )
    {
        var state = await library.LoadAsync(ct);
        foreach (var mod in state.Mods.Where(m => modIds is null || modIds.Contains(m.Id)))
            foreach (var source in mod.Sources.Where(s => providers.TryGetValue(s.Provider, out var provider) && provider.SupportsUpdateChecks))
            {
                ProviderUpdate? update = null;
                string? error = null;
                try
                {
                    update = await GetProvider(source.Provider).CheckUpdateAsync(source, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                }
                await library.RecordUpdateCheckAsync(
                    new(
                        mod.Id,
                        source.Provider,
                        DateTimeOffset.UtcNow,
                        update?.File?.Version,
                        update?.File?.FileId,
                        error ?? update?.Reason
                    ),
                    ct
                );
            }
    }

    public async Task<DownloadJob> QueueUpdateAsync(Guid modId, CancellationToken ct = default, IReadOnlyList<ModDependency>? dependencies = null)
    {
        var state = await library.LoadAsync(ct);
        var mod = state.Mods.Single(m => m.Id == modId);
        var check =
            state.UpdateChecks.FirstOrDefault(c =>
                c.ModId == modId
                && c.AvailableFileId is not null
                && c.Error is null
                && mod.Sources.Any(s => s.Provider == c.Provider && s.FileId != c.AvailableFileId)
            )
            ?? throw new InvalidOperationException(
                "No update is available for this mod. Check for updates again."
            );
        var source = mod.Sources.Single(s => s.Provider == check.Provider);
        var update = await GetProvider(source.Provider).CheckUpdateAsync(source, ct);
        if (update is not { Status: UpdateStatus.Available, File: { } file })
            throw new InvalidOperationException(
                "The update is no longer available. Check for updates again."
            );
        if (dependencies is not null) file = file with { Dependencies = dependencies };
        if (file.Provider == NexusAdapter.ProviderId && file.Dependencies is null)
            file = file with { Dependencies = (await GetRequirementsAsync(file.DownloadPage.AbsoluteUri, ct))
                .Select(requirement => new ModDependency(requirement.Name, requirement.Page.AbsoluteUri, requirement.Notes, requirement.CanInstall)).ToArray() };
        return await QueueAsync(file with { Name = mod.Name }, replacesModId: mod.Id, ct: ct,
            installedAsDependency: mod.InstalledAsDependency);
    }

    public async Task WaitForJobAsync(Guid id)
    {
        Task? task;
        await gate.WaitAsync();
        try
        {
            task = workers.GetValueOrDefault(id).Task;
        }
        finally
        {
            gate.Release();
        }
        if (task is not null)
            await task;
    }

    private void Start(
        DownloadJob job,
        string? grant = null,
        string? archive = null,
        CancellationToken ct = default
    )
    {
        if (grant is null && archive is null && DownloadsDirectly(job.File))
            grant = job.File.DownloadPage.AbsoluteUri;
        var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct);
        workers[job.Id] = (source, Task.Run(() => RunAsync(job, grant, archive, source.Token)));
    }

    private async Task RunAsync(
        DownloadJob job,
        string? grant,
        string? archive,
        CancellationToken ct
    )
    {
        var path = Path.Combine(cacheDirectory, job.Id.ToString("N") + ".zip");
        try
        {
            var provider = GetProvider(job.File.Provider);
            var filename = archive is not null ? Path.GetFileName(archive) : job.File.FileName;
            if (archive is not null)
            {
                await SetStatusAsync(job.Id, DownloadStatus.Importing, ct: ct);
                Directory.CreateDirectory(cacheDirectory);
                await using var input = new FileStream(
                    archive,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read
                );
                if (input.Length > 8L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Archive exceeds the size limit.");
                await using var output = new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                );
                await input.CopyToAsync(output, ct);
            }
            else if (grant is null)
            {
                var scanner = provider.CreateScanner();
                if (job.File.Provider == ManualDownloads.ProviderId)
                {
                    var mods = (await library.LoadAsync(ct)).Mods;
                    scanner = new ManualDownloadScanner(job.ExistingFiles, mods.Where(mod => !mod.Superseded)
                        .Select(mod => (mod.Id, mod.ImportedFileName ?? mod.Name + ".zip", mod.ImportedAt)).ToArray());
                }
                filename = await scanner.WaitForDownloadAsync(job.File, () => State.Directories, path, ct);
            }
            else
            {
                await SetStatusAsync(job.Id, DownloadStatus.Downloading, ct: ct);
                await provider.DownloadAsync(grant, job.File, path, ct);
                if (job.File.Size is { } size && new FileInfo(path).Length != size)
                    throw new InvalidDataException(
                        "Download size does not match the requested file."
                    );
            }
            await SetStatusAsync(job.Id, DownloadStatus.Importing, ct: ct);
            await imports.WaitAsync(ct);
            try
            {
                var importName = job.File.Provider == ManualDownloads.ProviderId &&
                    (job.File.Name == DownloadNames.DisplayName(job.File.FileName) || job.File.Name == Path.GetFileNameWithoutExtension(job.File.FileName))
                    ? DownloadNames.DisplayName(filename) : job.File.Name;
                job = State.Jobs.Single(item => item.Id == job.Id);
                var mod = await library.ImportAsync(path, importName, ct, job.ProfileId,
                    installedAsDependency: job.InstalledAsDependency);
                foreach (var profileId in job.AdditionalProfileIds)
                    await library.AddToProfileAsync(mod.Id, profileId, ct);
                if (job.File.Provider != ManualDownloads.ProviderId) await library.SetSourcesAsync(
                    mod.Id,
                    [
                        .. mod.Sources.Where(s => s.Provider != job.File.Provider),
                        new(job.File.Provider, job.File.ModId, job.File.FileId, job.File.Version),
                    ],
                    ct
                );
                if (job.File.Dependencies is { } dependencies) await library.SetDependenciesAsync(mod.Id, dependencies, ct);
                await library.SetDownloadMetadataAsync(mod.Id, filename, job.File.PageLink?.AbsoluteUri ??
                    (job.File.Provider == ManualDownloads.ProviderId ? job.File.DownloadPage.AbsoluteUri : null), ct);
                if (job.ReplacesModId is { } old)
                {
                    if (old != mod.Id)
                        await library.ReplaceInProfilesAsync(old, mod.Id, ct);
                    await library.RecordUpdateCheckAsync(
                        new(old, job.File.Provider, DateTimeOffset.UtcNow, null, null),
                        ct
                    );
                }
            }
            finally { imports.Release(); }
            await SetStatusAsync(job.Id, DownloadStatus.Complete, ct: ct);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (archive is not null && !lifetime.IsCancellationRequested)
                await SetStatusAsync(job.Id, DownloadStatus.Failed, "Attaching ZIP was cancelled.");
        }
        catch (ManualUpdateWarning warning)
        {
            await MutateAsync(state => state with
            {
                Jobs = state.Jobs.Select(item => item.Id == job.Id ? item with
                {
                    Status = DownloadStatus.NeedsConfirmation,
                    Error = null,
                    Warning = warning.Message,
                    ConfirmationFile = warning.File
                } : item).ToArray()
            }, ct);
        }
        catch (Exception ex)
        {
            await SetStatusAsync(job.Id, DownloadStatus.Failed, ex.Message);
            DownloadFailed?.Invoke(State.Jobs.Single(j => j.Id == job.Id));
            LibraryChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private async Task StopWorkerAsync(Guid id, bool preserveImporting = false)
    {
        (CancellationTokenSource Cancellation, Task Task) worker;
        await gate.WaitAsync();
        try
        {
            if (
                preserveImporting
                && State.Jobs.Any(job => job.Id == id && job.Status == DownloadStatus.Importing)
            )
                throw new InvalidOperationException("This download is already being imported.");
            if (!workers.Remove(id, out worker))
                return;
            worker.Cancellation.Cancel();
        }
        finally
        {
            gate.Release();
        }
        await worker.Task;
        worker.Cancellation.Dispose();
    }

    private Task SetStatusAsync(
        Guid id,
        DownloadStatus status,
        string? error = null,
        CancellationToken ct = default
    ) =>
        MutateAsync(
            state =>
                state with
                {
                    Jobs = state
                        .Jobs.Select(j =>
                            j.Id == id ? j with { Status = status, Error = error, Warning = null, ConfirmationFile = null } : j
                        )
                        .ToArray(),
                },
            ct
        );

    private async Task MutateAsync(Func<DownloadState, DownloadState> change, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var updated = change(State);
            await store.SaveAsync(updated, ct);
            State = updated;
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private IModProvider GetProvider(string id) =>
        providers.GetValueOrDefault(id) ?? throw new NotSupportedException("Unknown mod provider.");

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        var pending = workers.Values.ToArray();
        workers.Clear();
        gate.Release();
        await Task.WhenAll(pending.Select(w => w.Task)).ConfigureAwait(false);
        foreach (var worker in pending)
            worker.Cancellation.Dispose();
        // Desktop exit waits synchronously on the UI thread; keep the final storage write off that context.
        await Task.Run(() =>
                MutateAsync(
                    state =>
                        state with
                        {
                            Jobs = state
                                .Jobs.Where(job =>
                                    job.Status
                                        is not (
                                            DownloadStatus.Waiting
                                            or DownloadStatus.Downloading
                                            or DownloadStatus.Importing
                                            or DownloadStatus.NeedsConfirmation
                                        )
                                )
                                .ToArray(),
                        },
                    CancellationToken.None
                )
            )
            .ConfigureAwait(false);
        lifetime.Dispose();
    }
}
