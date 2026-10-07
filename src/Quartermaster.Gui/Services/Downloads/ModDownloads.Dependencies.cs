using Quartermaster.Library.Mods;
using Quartermaster.Providers.Clients;
using Quartermaster.Providers.Clients.NexusMods;

namespace Quartermaster.Gui.Services;

public sealed partial class ModDownloads
{
    private sealed record Dependency(ModRequirement Requirement, Mod? Installed);
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
            dependencies.AddRange(await PlanNexusDependenciesAsync(new(mod.Id.ToString(), mod.Name, mod.Description, mod.Version, new(page), []), profileId, ct, metadata));
        }
        var unique = dependencies.DistinctBy(item => item.Requirement.Page.AbsoluteUri).ToArray();
        var files = new List<ProviderFile>();
        foreach (var dependency in unique.Where(item => item.Installed is null))
        {
            var mod = await services.Providers.ResolveAsync(dependency.Requirement.Page.AbsoluteUri, ct);
            var file = await services.Dialogs.ChooseModFileAsync(mod);
            if (file is null) return;
            files.Add(file with { Name = mod.Name, Dependencies = metadata[file.ModId] });
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
    private async Task<IReadOnlyList<Dependency>> PlanNexusDependenciesAsync(ProviderMod root, Guid? profileId, CancellationToken ct, Dictionary<string, IReadOnlyList<ModDependency>> metadata, IReadOnlyList<Guid>? targetProfileIds = null, string declineLabel = "Install mod only")
    {
        var visited = new HashSet<long> { NexusLink.Parse(root.Page.AbsoluteUri).ModId };
        var missing = new List<Dependency>();
        var external = new Dictionary<string, ModRequirement>();
        var profileIds = targetProfileIds ?? (profileId is { } id ? new[] { id } : []);
        var profiles = services.Session.State.Profiles.Where(profile => profileIds.Contains(profile.Id)).ToArray();
        var profileMods = profiles.Length > 0
            ? profiles.Select(profile => profile.Entries.Where(entry => entry.Enabled).Select(entry => entry.ModId).ToHashSet())
                .Aggregate((left, right) => { left.IntersectWith(right); return left; }) : null;
        async Task Visit(string page)
        {
            var requirements = await services.Providers.GetRequirementsAsync(page, ct);
            var snapshot = requirements.Select(requirement => new ModDependency(requirement.Name, requirement.Page.AbsoluteUri, requirement.Notes, requirement.CanInstall)).ToArray();
            metadata[NexusLink.Parse(page).ModId.ToString()] = snapshot;
            var installedOwner = services.Session.State.Mods.Where(mod => MatchesNexusMod(mod, NexusLink.Parse(page).ModId)).ToArray();
            foreach (var owner in installedOwner)
                await services.Library.SetDependenciesAsync(owner.Id, snapshot, ct);
            foreach (var requirement in requirements)
            {
                ct.ThrowIfCancellationRequested();
                if (!requirement.CanInstall)
                { external.TryAdd(requirement.Page.AbsoluteUri, requirement); continue; }
                var modId = NexusLink.Parse(requirement.Page.AbsoluteUri).ModId;
                if (!visited.Add(modId)) continue;
                if (visited.Count > 256) throw new InvalidDataException("This mod's dependency list is too large to install together.");
                var installed = services.Session.State.Mods.FirstOrDefault(mod => !mod.Superseded && ModDependencyMatching.Matches(mod, new(requirement.Name, requirement.Page.AbsoluteUri)));
                var pending = services.Providers.State.Jobs.Any(job => job.File.Provider == "nexusmods" && job.File.ModId == modId.ToString() &&
                    (profileIds.Count == 0 || profileIds.All(id => job.ProfileId == id || job.AdditionalProfileIds.Contains(id))) &&
                    job.Status is Quartermaster.Providers.Downloads.DownloadStatus.Waiting or
                        Quartermaster.Providers.Downloads.DownloadStatus.Downloading or Quartermaster.Providers.Downloads.DownloadStatus.Importing);
                await Visit(requirement.Page.AbsoluteUri);
                if (!pending && (installed is null || profileMods is not null && !profileMods.Contains(installed.Id)))
                    missing.Add(new(requirement, installed));
            }
        }
        await Visit(root.Page.AbsoluteUri);
        await services.Session.ReloadAsync(ct);
        if (missing.Count == 0 && external.Count == 0) return [];
        var message = missing.Count > 0
            ? $"{root.Name} requires:\n" + string.Join("\n", missing.Select(item => "• " + item.Requirement.Name +
                (item.Installed is null ? "" : " (already in your library)") +
                (string.IsNullOrWhiteSpace(item.Requirement.Notes) ? "" : " — " + item.Requirement.Notes))) +
                (profileIds.Count == 0 ? "\n\nInstall these alongside it?" : "\n\nInstall and add these to the same profiles?")
            : $"{root.Name} has additional requirements.";
        if (external.Count > 0)
            message += "\n\nRequirements to install manually:\n" + string.Join("\n", external.Values.Select(item =>
                $"• {item.Name}: {item.Page}" + (string.IsNullOrWhiteSpace(item.Notes) ? "" : " — " + item.Notes)));
        return await services.Dialogs.ConfirmAsync("Mod requirements", message,
            missing.Count > 0 ? "Include dependencies" : "Continue", declineLabel) ? missing : [];
    }
    public bool CanResolveDependencies(Mod mod) => NexusPage(mod) is not null;

    public async Task ResolveDependenciesAsync(Guid modId, CancellationToken ct)
    {
        var mod = services.Session.State.Mods.Single(item => item.Id == modId);
        await PrepareModDependenciesAsync(mod, ct, "Not now");
    }

    private static string? NexusPage(Mod mod)
    {
        var source = mod.Sources.FirstOrDefault(source => source.Provider == "nexusmods");
        var page = source is not null ? $"https://www.nexusmods.com/{NexusLink.Game}/mods/{source.ModId}" : ModLinks.PageFor(mod);
        if (page is null) return null;
        try { return NexusLink.Parse(page).Page.AbsoluteUri; }
        catch (ArgumentException) { return null; }
    }

    private async Task<Dictionary<string, IReadOnlyList<ModDependency>>?> PrepareModDependenciesAsync(Mod root, CancellationToken ct, string declineLabel)
    {
        var metadata = new Dictionary<string, IReadOnlyList<ModDependency>>();
        if (NexusPage(root) is not { } page) return metadata;
        var profileIds = services.Session.State.Profiles.Where(profile => profile.Entries.Any(entry => entry.ModId == root.Id))
            .Select(profile => profile.Id).ToArray();
        var dependencies = await PlanNexusDependenciesAsync(new(root.Id.ToString(), root.Name, root.Description, root.Version, new(page), []),
            null, ct, metadata, profileIds, declineLabel);
        var files = new List<ProviderFile>();
        foreach (var dependency in dependencies.Where(item => item.Installed is null))
        {
            var resolved = await services.Providers.ResolveAsync(dependency.Requirement.Page.AbsoluteUri, ct);
            var selected = await services.Dialogs.ChooseModFileAsync(resolved);
            if (selected is null) return null;
            files.Add(selected with { Name = resolved.Name, Dependencies = metadata[selected.ModId] });
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
        var metadata = await PrepareModDependenciesAsync(mod, ct, "Update mod only");
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

    private static bool MatchesNexusMod(Mod mod, long id)
    {
        return ModDependencyMatching.MatchesPage(mod, new("", $"https://www.nexusmods.com/{NexusLink.Game}/mods/{id}"));
    }
}
