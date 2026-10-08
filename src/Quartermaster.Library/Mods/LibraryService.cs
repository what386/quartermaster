using Quartermaster.Core.Patching;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public sealed class LibraryService(ILibraryStore store, IModContentStore contents)
{
    public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => store.LoadAsync(cancellationToken);

    public async Task<Mod> ImportAsync(string source, string? name = null, CancellationToken cancellationToken = default, Guid? profileId = null, string? pageLink = null, bool installedAsDependency = false)
    {
        pageLink = ModLinks.ValidatePage(pageLink);
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var target = profileId is null ? null : state.Profiles.SingleOrDefault(p => p.Id == profileId)
            ?? throw new KeyNotFoundException("Profile does not exist.");
        var mod = await contents.ImportAsync(source, name, cancellationToken).ConfigureAwait(false);
        mod = mod with { InstalledAsDependency = installedAsDependency };
        if (pageLink is not null) mod = mod with { PageLink = pageLink };
        var identity = ModIdentity.GetKey(mod);
        var existing = state.Mods.FirstOrDefault(item => ModIdentity.GetKey(item) == identity);
        if (existing is not null)
        {
            await contents.DeleteAsync(mod.Id, CancellationToken.None).ConfigureAwait(false);
            var updatedExisting = existing with
            {
                PageLink = pageLink ?? existing.PageLink,
                Tags = ModTags.Normalize(existing.Tags.Concat(mod.Tags)),
                InstalledAsDependency = existing.InstalledAsDependency && installedAsDependency
            };
            var duplicateState = state;
            if (updatedExisting != existing) duplicateState = duplicateState with
            { Mods = state.Mods.Select(item => item.Id == existing.Id ? updatedExisting : item).ToArray() };
            if (target is not null && target.Entries.All(entry => entry.ModId != existing.Id)) duplicateState = duplicateState with
            { Profiles = state.Profiles.Select(p => p.Id == target.Id ? ProfileEditor.Add(p, updatedExisting) : p).ToArray() };
            if (duplicateState != state) await store.SaveAsync(duplicateState, cancellationToken).ConfigureAwait(false);
            return updatedExisting;
        }
        var updated = state with { Mods = [.. state.Mods, mod] };
        if (target is not null) updated = updated with
        { Profiles = state.Profiles.Select(p => p.Id == target.Id ? ProfileEditor.Add(p, mod) : p).ToArray() };
        try { await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false); }
        catch { await contents.DeleteAsync(mod.Id, CancellationToken.None).ConfigureAwait(false); throw; }
        return mod;
    }

    public Task AddToProfileAsync(Guid modId, Guid profileId, CancellationToken cancellationToken = default) =>
        AddToProfileAsync([modId], profileId, cancellationToken);

    public async Task AddToProfileAsync(IReadOnlyCollection<Guid> modIds, Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var ids = modIds.ToHashSet();
        if (ids.Any(id => state.Mods.All(mod => mod.Id != id))) throw new KeyNotFoundException("Mod is not in the library.");
        var profile = state.Profiles.SingleOrDefault(p => p.Id == profileId) ?? throw new KeyNotFoundException("Profile does not exist.");
        var original = profile;
        foreach (var mod in state.Mods.Where(mod => ids.Contains(mod.Id) && profile.Entries.All(entry => entry.ModId != mod.Id)))
            profile = ProfileEditor.Add(profile, mod);
        if (profile == original) return;
        await store.SaveAsync(state with
        { Profiles = state.Profiles.Select(p => p.Id == profileId ? profile : p).ToArray() }, cancellationToken).ConfigureAwait(false);
    }

    public Task RemoveAsync(Guid modId, CancellationToken cancellationToken = default) => RemoveAsync([modId], cancellationToken);

    public async Task RemoveAsync(IReadOnlyCollection<Guid> modIds, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var ids = modIds.ToHashSet();
        if (ids.Any(id => state.Mods.All(mod => mod.Id != id))) throw new KeyNotFoundException("Mod is not in the library.");
        if (ids.Count == 0) return;
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Where(m => !ids.Contains(m.Id)).ToArray(),
            UpdateChecks = state.UpdateChecks.Where(c => !ids.Contains(c.ModId)).ToArray(),
            Profiles = state.Profiles.Select(p => p with { Entries = p.Entries.Where(entry => !ids.Contains(entry.ModId)).ToArray() }).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        foreach (var id in ids) await contents.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveProfileAsync(Profile profile, bool makeActive = false, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var profiles = state.Profiles.ToList();
        var index = profiles.FindIndex(p => p.Id == profile.Id);
        if (index < 0) profiles.Add(profile); else profiles[index] = profile;
        await store.SaveAsync(state with { Profiles = profiles.ToArray(), ActiveProfileId = makeActive ? profile.Id : state.ActiveProfileId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Profiles.All(p => p.Id != profileId)) throw new KeyNotFoundException("Profile does not exist.");
        await store.SaveAsync(state with
        {
            Profiles = state.Profiles.Where(p => p.Id != profileId).ToArray(),
            ActiveProfileId = state.ActiveProfileId == profileId ? null : state.ActiveProfileId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSourcesAsync(Guid modId, IReadOnlyList<SourceReference> sources, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Mods.All(m => m.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Select(m => m.Id == modId ? m with
            { Sources = sources.ToArray(), PageLink = m.PageLink ?? ModLinks.PageFor(m with { Sources = sources }) } : m).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetDependenciesAsync(Guid modId, IReadOnlyList<ModDependency> dependencies, CancellationToken ct = default)
    {
        ModDependency Validate(ModDependency dependency)
        {
            if (!Uri.TryCreate(dependency.Page, UriKind.Absolute, out var page) ||
                page.Scheme is not ("https" or "http") || page.UserInfo.Length > 0)
                throw new ArgumentException("A dependency needs a public HTTP or HTTPS page.");
            return dependency with { Page = page.AbsoluteUri, Alternatives = dependency.Alternatives.Select(Validate).ToArray() };
        }
        var validated = ModDependencyExclusions.Filter(dependencies).Select(Validate).ToArray();
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (state.Mods.All(mod => mod.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Select(mod => mod.Id == modId
            ? mod with { Dependencies = validated, DependenciesKnown = true } : mod).ToArray()
        }, ct).ConfigureAwait(false);
    }

    public async Task SetTagsAsync(Guid modId, IEnumerable<string> tags, CancellationToken ct = default)
    {
        var normalized = ModTags.Normalize(tags);
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (state.Mods.All(mod => mod.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with { Mods = state.Mods.Select(mod => mod.Id == modId ? mod with { Tags = normalized } : mod).ToArray() }, ct).ConfigureAwait(false);
    }

    public async Task SetPageLinkAsync(Guid modId, string? link, CancellationToken ct = default)
    {
        link = ModLinks.ValidatePage(link);
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (state.Mods.All(mod => mod.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with { Mods = state.Mods.Select(mod => mod.Id == modId ? mod with { PageLink = link } : mod).ToArray() }, ct).ConfigureAwait(false);
    }

    public async Task SetDownloadMetadataAsync(Guid modId, string filename, string? page, CancellationToken ct = default)
    {
        page = ModLinks.ValidatePage(page);
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (state.Mods.All(mod => mod.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Select(mod => mod.Id == modId ? mod with
            { ImportedFileName = filename, PageLink = page ?? mod.PageLink } : mod).ToArray()
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Updates profile references while preserving order, groups and compatible option selections.</summary>
    public async Task ReplaceInProfilesAsync(Guid oldId, Guid newId, CancellationToken cancellationToken = default)
    {
        if (oldId == newId) return;
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var oldMod = state.Mods.Single(m => m.Id == oldId);
        var newMod = state.Mods.Single(m => m.Id == newId);
        ProfileEntry Replace(ProfileEntry entry)
        {
            var options = entry.Options.Select(selection =>
            {
                var oldOption = oldMod.Options.Single(o => o.Id == selection.OptionId);
                var option = newMod.Options.SingleOrDefault(o => o.Name == oldOption.Name)
                    ?? throw new InvalidOperationException("Updated mod options changed. Configure the new mod before replacing it in profiles.");
                var choice = oldOption.Choices.Count == 0 ? 0 : option.Choices.ToList().FindIndex(c => c.Name == oldOption.Choices[selection.ChoiceIndex].Name);
                if (choice < 0) throw new InvalidOperationException("An updated mod option no longer offers the selected choice.");
                return new OptionSelection(option.Id, selection.Enabled, choice);
            }).ToArray();
            var replacement = entry with { ModId = newId, Options = options };
            PatchSelection.Select(newMod, replacement);
            return replacement;
        }
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Select(mod => mod.Id == oldId ? mod with { Superseded = true } :
                mod.Id == newId ? mod with
                {
                    Superseded = false,
                    PageLink = oldMod.PageLink ?? mod.PageLink,
                    Tags = ModTags.Normalize(oldMod.Tags.Concat(mod.Tags)),
                    Dependencies = mod.DependenciesKnown ? mod.Dependencies : oldMod.Dependencies,
                    DependenciesKnown = mod.DependenciesKnown || oldMod.DependenciesKnown,
                    InstalledAsDependency = mod.InstalledAsDependency && oldMod.InstalledAsDependency
                } : mod).ToArray(),
            Profiles = state.Profiles.Select(profile => profile.Entries.Any(e => e.ModId == oldId)
                ? profile with { Entries = profile.Entries.Where(e => e.ModId != newId).Select(e => e.ModId == oldId ? Replace(e) : e).ToArray() }
                : profile).ToArray()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordUpdateCheckAsync(UpdateCheck check, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        await store.SaveAsync(state with { UpdateChecks = [.. state.UpdateChecks.Where(c => c.ModId != check.ModId || c.Provider != check.Provider), check] }, cancellationToken).ConfigureAwait(false);
    }
}

public interface ILibraryStore
{
    Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LibraryState state, CancellationToken cancellationToken = default);
    ValueTask<IAsyncDisposable> AcquireLockAsync(CancellationToken cancellationToken = default);
}
public interface IModContentStore
{
    Task<Mod> ImportAsync(string source, string? name = null, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid modId, CancellationToken cancellationToken = default);
}
