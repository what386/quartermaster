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
        await services.Session.AddModsToProfileAsync(unique.Where(item => item.Installed is not null)
            .Select(item => item.Installed!.Id).Concat(modIds).Distinct().ToArray(), profileId, ct);
        foreach (var file in files)
        {
            var job = await services.Providers.QueueAsync(file, profileId, ct: ct);
            if (!services.Providers.DownloadsDirectly(job.File)) services.Providers.OpenDownloadPage(job.Id);
        }
    }
    private async Task<IReadOnlyList<Dependency>> PlanNexusDependenciesAsync(ProviderMod root, Guid? profileId, CancellationToken ct, Dictionary<string, IReadOnlyList<ModDependency>> metadata)
    {
        var visited = new HashSet<long> { NexusLink.Parse(root.Page.AbsoluteUri).ModId };
        var missing = new List<Dependency>();
        var external = new Dictionary<string, ModRequirement>();
        var profileMods = profileId is { } id
            ? services.Session.State.Profiles.Single(profile => profile.Id == id).Entries.Select(entry => entry.ModId).ToHashSet() : null;
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
                var installed = services.Session.State.Mods.FirstOrDefault(mod => !mod.Superseded && MatchesNexusMod(mod, modId));
                var pending = services.Providers.State.Jobs.Any(job => job.File.Provider == "nexusmods" && job.File.ModId == modId.ToString() &&
                    (profileId is null || job.ProfileId == profileId) &&
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
                (profileId is null ? "\n\nInstall these alongside it?" : "\n\nAdd these to this profile alongside it?")
            : $"{root.Name} has additional requirements.";
        if (external.Count > 0)
            message += "\n\nRequirements to install manually:\n" + string.Join("\n", external.Values.Select(item =>
                $"• {item.Name}: {item.Page}" + (string.IsNullOrWhiteSpace(item.Notes) ? "" : " — " + item.Notes)));
        return await services.Dialogs.ConfirmAsync("Mod requirements", message,
            missing.Count > 0 ? "Include dependencies" : "Continue", "Install mod only") ? missing : [];
    }
    private static bool MatchesNexusMod(Mod mod, long id)
    {
        if (mod.Sources.Any(source => source.Provider == "nexusmods" && source.ModId == id.ToString())) return true;
        try { return ModLinks.PageFor(mod) is { } page && NexusLink.Parse(page).ModId == id; }
        catch (ArgumentException) { return false; }
    }
}
