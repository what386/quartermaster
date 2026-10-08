using Quartermaster.Library.Mods;
using Quartermaster.Gui.Mods;
using Quartermaster.Providers.Clients;
using Quartermaster.Providers.Clients.NexusMods;

namespace Quartermaster.Gui.Services;

public sealed partial class ModDownloads
{
    private sealed record Dependency(ModRequirement Requirement, Mod? Installed, ProviderFile? File = null);
    public async Task AddLibraryModsToProfileAsync(IReadOnlyCollection<Guid> modIds, Guid profileId, CancellationToken ct)
    {
        var dependencies = new List<Dependency>();
        var metadata = new Dictionary<string, IReadOnlyList<ModDependency>>();
        foreach (var mod in services.Session.State.Mods.Where(mod => modIds.Contains(mod.Id)).ToArray())
        {
            var source = mod.Sources.FirstOrDefault(source => source.Provider == "nexusmods");
            if (source is null) continue;
            var page = $"https://www.nexusmods.com/{NexusLink.Game}/mods/{source.ModId}";
            try { NexusLink.Parse(page); }
            catch (ArgumentException) { continue; }
            var planned = await PlanNexusDependenciesAsync(new(mod.Id.ToString(), mod.Name, mod.Description, mod.Version,
                RequirementPage(page, source.FileId), []), profileId, ct, metadata);
            if (planned is null) return;
            dependencies.AddRange(planned);
        }
        var unique = dependencies.DistinctBy(item => item.Requirement.Page.AbsoluteUri).ToArray();
        var files = new List<ProviderFile>();
        foreach (var dependency in unique.Where(item => item.Installed is null))
        {
            var file = dependency.File!;
            files.Add(file with { Dependencies = metadata[file.ModId] });
        }
        ct.ThrowIfCancellationRequested();
        await IncludeDependenciesInProfileAsync(unique.Where(item => item.Installed is not null)
            .Select(item => item.Installed!.Id).Distinct().ToArray(), profileId, ct);
        await services.Session.AddModsToProfileAsync(modIds, profileId, ct);
        foreach (var file in files)
        {
            var job = await services.Providers.QueueAsync(file, profileId, ct: ct, installedAsDependency: true);
            if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
        }
    }
    private async Task<IReadOnlyList<Dependency>?> PlanNexusDependenciesAsync(ProviderMod root, Guid? profileId, CancellationToken ct, Dictionary<string, IReadOnlyList<ModDependency>> metadata, IReadOnlyList<Guid>? targetProfileIds = null, string declineLabel = "Install mod only")
    {
        var visited = new HashSet<long> { NexusLink.Parse(root.Page.AbsoluteUri).ModId };
        var missing = new List<Dependency>();
        var external = new Dictionary<string, ModRequirement>();
        var choices = new Dictionary<string, string>();
        var filesByMod = new Dictionary<string, IReadOnlyList<ProviderFile>>();
        DependencyReviewViewModel? review = null;
        var profileIds = targetProfileIds ?? (profileId is { } id ? new[] { id } : []);
        var profiles = services.Session.State.Profiles.Where(profile => profileIds.Contains(profile.Id)).ToArray();
        var profileMods = profiles.Length > 0
            ? profiles.Select(profile => profile.Entries.Where(entry => entry.Enabled).Select(entry => entry.ModId).ToHashSet())
                .Aggregate((left, right) => { left.IntersectWith(right); return left; }) : null;
        async Task Visit(string page)
        {
            var requirements = await services.Providers.GetRequirementsAsync(page, ct);
            var snapshot = requirements.Select(requirement => requirement.ToDependency()).ToArray();
            metadata[NexusLink.Parse(page).ModId.ToString()] = snapshot;
            var link = NexusLink.Parse(page);
            var installedOwner = services.Session.State.Mods.Where(mod => MatchesNexusMod(mod, link.ModId) &&
                (link.FileId is null || mod.Sources.Any(source => source.Provider == "nexusmods" && source.FileId == link.FileId.ToString()))).ToArray();
            // Keep fetched metadata even when file selection or installation is declined.
            foreach (var owner in installedOwner)
                await services.Library.SetDependenciesAsync(owner.Id, snapshot, ct);
            foreach (var (requirement, requirementIndex) in requirements.Select((value, index) => (value, index)))
            {
                ct.ThrowIfCancellationRequested();
                if (!requirement.CanInstall)
                { external.TryAdd(requirement.Page.AbsoluteUri, requirement); continue; }
                var alternatives = new[] { requirement }.Concat(requirement.Alternatives).Where(item => item.CanInstall).ToArray();
                var selectedRequirement = alternatives.FirstOrDefault(item => services.Session.State.Mods.Any(mod =>
                    !mod.Superseded && ModDependencyMatching.Matches(mod, item.ToDependency() with { Alternatives = [] }))) ?? alternatives[0];
                var modId = NexusLink.Parse(selectedRequirement.Page.AbsoluteUri).ModId;
                if (!visited.Add(modId)) continue;
                if (visited.Count > 256) throw new InvalidDataException(Localizer.Text("This mod's dependency list is too large to install together."));
                var installed = services.Session.State.Mods.FirstOrDefault(mod => !mod.Superseded && ModDependencyMatching.Matches(mod, selectedRequirement.ToDependency() with { Alternatives = [] }));
                var pending = services.Providers.State.Jobs.Any(job => job.File.Provider == "nexusmods" && job.File.ModId == modId.ToString() &&
                    (selectedRequirement.AllowedFileIds is null || selectedRequirement.AllowedFileIds.Contains(job.File.FileId)) &&
                    (profileIds.Count == 0 || profileIds.All(id => job.ProfileId == id || job.AdditionalProfileIds.Contains(id))) &&
                    job.Status is Quartermaster.Providers.Downloads.DownloadStatus.Waiting or
                        Quartermaster.Providers.Downloads.DownloadStatus.Downloading or Quartermaster.Providers.Downloads.DownloadStatus.Importing);
                ProviderFile? selectedFile = null;
                var childPage = selectedRequirement.Page;
                if (installed is not null)
                    childPage = RequirementPage(childPage.AbsoluteUri, installed.Sources.FirstOrDefault(source => source.Provider == "nexusmods")?.FileId);
                else if (!pending)
                {
                    ProviderMod? resolved = null;
                    foreach (var alternative in alternatives.OrderByDescending(item => ReferenceEquals(item, selectedRequirement)))
                    {
                        var candidate = await services.Providers.ResolveAsync(alternative.Page.AbsoluteUri, ct);
                        var compatible = candidate.Files.Where(file => file.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                            (alternative.AllowedFileIds is null || alternative.AllowedFileIds.Contains(file.FileId))).ToArray();
                        if (compatible.Length == 0) continue;
                        selectedRequirement = alternative;
                        modId = NexusLink.Parse(alternative.Page.AbsoluteUri).ModId;
                        visited.Add(modId);
                        resolved = candidate with { Files = compatible };
                        break;
                    }
                    if (resolved is null)
                    {
                        var manual = selectedRequirement with
                        {
                            CanInstall = false,
                            Notes = Localizer.Text("No compatible ZIP is available. Install this requirement manually.")
                        };
                        external.TryAdd(manual.Page.AbsoluteUri, manual);
                        snapshot[requirementIndex] = manual.ToDependency();
                        continue;
                    }
                    var compatibleFiles = resolved.Files.Select(file => file with { Name = resolved.Name }).ToArray();
                    filesByMod[resolved.ModId] = compatibleFiles;
                    selectedFile = compatibleFiles.FirstOrDefault(file => choices.GetValueOrDefault(resolved.ModId) == file.FileId)
                        ?? compatibleFiles.FirstOrDefault(file => file.IsPrimary) ?? compatibleFiles[0];
                    childPage = selectedFile.DownloadPage;
                }
                else
                    childPage = services.Providers.State.Jobs.First(job => job.File.Provider == "nexusmods" && job.File.ModId == modId.ToString() &&
                        (selectedRequirement.AllowedFileIds is null || selectedRequirement.AllowedFileIds.Contains(job.File.FileId)) &&
                        job.Status is Quartermaster.Providers.Downloads.DownloadStatus.Waiting or Quartermaster.Providers.Downloads.DownloadStatus.Downloading or
                            Quartermaster.Providers.Downloads.DownloadStatus.Importing).File.DownloadPage;
                await Visit(childPage.AbsoluteUri);
                if (!pending && (installed is null || profileMods is not null && !profileMods.Contains(installed.Id)))
                    missing.Add(new(selectedRequirement, installed, selectedFile));
            }
            foreach (var owner in installedOwner)
                await services.Library.SetDependenciesAsync(owner.Id, snapshot, ct);
        }
        async Task<DependencyReviewPlan> BuildPlan()
        {
            visited.Clear(); visited.Add(NexusLink.Parse(root.Page.AbsoluteUri).ModId);
            missing.Clear(); external.Clear(); filesByMod.Clear();
            await Visit(root.Page.AbsoluteUri);
            await services.Session.ReloadAsync(ct);
            var items = missing.Select(item => new DependencyReviewItem(item.Requirement.Name,
                item.Installed is null ? (profileIds.Count == 0 ? Localizer.Text("Download to library") : Localizer.Text("Download and add to profile")) :
                    profiles.Any(profile => profile.Entries.Any(entry => entry.ModId == item.Installed.Id && !entry.Enabled))
                        ? Localizer.Text("Enable in profile (already in your library)") : Localizer.Text("Add to profile (already in your library)"),
                item.File is { } file ? filesByMod[file.ModId] : [], item.File,
                file => review!.ChangeFileAsync(file))).ToArray();
            return new(items, external.Values.ToArray());
        }
        var plan = await BuildPlan();
        if (missing.Count == 0 && external.Count == 0) return [];
        review = new(root.Name, missing.Count == 0 ? Localizer.Text("Continue") : profileIds.Count == 0 ? Localizer.Text("Install dependencies") : Localizer.Text("Add dependencies"),
            Localizer.Text(declineLabel), plan, async file =>
            {
                choices[file.ModId] = file.FileId;
                return await BuildPlan();
            });
        return await services.Dialogs.ReviewDependenciesAsync(review, ct) ? missing.ToArray() : [];
    }

    public bool CanResolveDependencies(Mod mod) => NexusPage(mod) is not null;

    public async Task ResolveDependenciesAsync(Guid modId, CancellationToken ct)
    {
        var mod = services.Session.State.Mods.Single(item => item.Id == modId);
        if (NexusPage(mod) is not { } page) return;
        var fileId = mod.Sources.FirstOrDefault(source => source.Provider == "nexusmods")?.FileId;
        var requirements = await services.Providers.GetRequirementsAsync(RequirementPage(page, fileId).AbsoluteUri, ct);
        await services.Library.SetDependenciesAsync(mod.Id, requirements.Select(item => item.ToDependency()).ToArray(), ct);
        await services.Session.ReloadAsync(ct);
    }

    public async Task GetDependenciesAsync(Guid modId, CancellationToken ct, Guid? profileId = null)
    {
        var mod = services.Session.State.Mods.Single(item => item.Id == modId);
        await PrepareModDependenciesAsync(mod, ct, Localizer.Text("Not now"), targetProfileIds: profileId is { } id ? [id] : []);
    }

    private static string? NexusPage(Mod mod)
    {
        var source = mod.Sources.FirstOrDefault(source => source.Provider == "nexusmods");
        var page = source is not null ? $"https://www.nexusmods.com/{NexusLink.Game}/mods/{source.ModId}" : ModLinks.PageFor(mod);
        if (page is null) return null;
        try { return NexusLink.Parse(page).Page.AbsoluteUri; }
        catch (ArgumentException) { return null; }
    }

    private async Task<Dictionary<string, IReadOnlyList<ModDependency>>?> PrepareModDependenciesAsync(Mod root, CancellationToken ct, string declineLabel, string? fileId = null, IReadOnlyList<Guid>? targetProfileIds = null)
    {
        var metadata = new Dictionary<string, IReadOnlyList<ModDependency>>();
        if (NexusPage(root) is not { } page) return metadata;
        var profileIds = targetProfileIds?.ToArray() ?? services.Session.State.Profiles.Where(profile => profile.Entries.Any(entry => entry.ModId == root.Id))
            .Select(profile => profile.Id).ToArray();
        fileId ??= root.Sources.FirstOrDefault(source => source.Provider == "nexusmods")?.FileId;
        var dependencies = await PlanNexusDependenciesAsync(new(root.Id.ToString(), root.Name, root.Description, root.Version, RequirementPage(page, fileId), []),
            null, ct, metadata, profileIds, declineLabel);
        if (dependencies is null) return null;
        var files = new List<ProviderFile>();
        foreach (var dependency in dependencies.Where(item => item.Installed is null))
        {
            var selected = dependency.File!;
            files.Add(selected with { Dependencies = metadata[selected.ModId] });
        }
        ct.ThrowIfCancellationRequested();
        foreach (var profileId in profileIds)
        {
            var installedIds = dependencies.Where(item => item.Installed is not null).Select(item => item.Installed!.Id).ToArray();
            if (installedIds.Length > 0) await IncludeDependenciesInProfileAsync(installedIds, profileId, ct);
        }
        foreach (var file in files)
        {
            var job = await services.Providers.QueueAsync(file, profileIds.Length > 0 ? profileIds[0] : null,
                ct: ct, installedAsDependency: true, additionalProfileIds: profileIds.Skip(1).ToArray());
            if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
        }
        return metadata;
    }

    private async Task QueueModUpdateAsync(Mod mod, CancellationToken ct)
    {
        var metadata = await PrepareModDependenciesAsync(mod, ct, Localizer.Text("Update mod only"), AvailableUpdate(mod)?.AvailableFileId);
        if (metadata is null) return;
        var source = mod.Sources.FirstOrDefault(source => source.Provider == "nexusmods");
        var dependencies = source is not null ? metadata.GetValueOrDefault(source.ModId) : null;
        var job = await services.Providers.QueueUpdateAsync(mod.Id, ct, dependencies);
        if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
    }

    private async Task IncludeDependenciesInProfileAsync(IReadOnlyCollection<Guid> modIds, Guid profileId, CancellationToken ct)
    {
        await services.Session.AddModsToProfileAsync(modIds, profileId, ct);
        var profile = services.Session.State.Profiles.Single(item => item.Id == profileId);
        var updated = profile;
        foreach (var entry in profile.Entries.Where(entry => modIds.Contains(entry.ModId) && !entry.Enabled))
            updated = Quartermaster.Library.Profiles.ProfileEditor.SetEnabled(updated, entry.ModId, true);
        if (updated != profile) await services.Session.SaveProfileAsync(updated, false, ct);
    }

    private static Uri RequirementPage(string page, string? fileId)
    {
        var link = NexusLink.Parse(page);
        return fileId is not null ? new Uri($"https://www.nexusmods.com/{NexusLink.Game}/mods/{link.ModId}?tab=files&file_id={fileId}") : link.Page;
    }

    private static bool MatchesNexusMod(Mod mod, long id)
    {
        return ModDependencyMatching.MatchesPage(mod, new("", $"https://www.nexusmods.com/{NexusLink.Game}/mods/{id}"));
    }
}
