using Quartermaster.Core.Patching;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public sealed class LibraryService(ILibraryStore store, IModContentStore contents)
{
    public Task<LibraryState> LoadAsync(CancellationToken cancellationToken = default) => store.LoadAsync(cancellationToken);

    public async Task<Mod> ImportAsync(string source, string? name = null, CancellationToken cancellationToken = default, Guid? profileId = null)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var target = profileId is null ? null : state.Profiles.SingleOrDefault(p => p.Id == profileId)
            ?? throw new KeyNotFoundException("Profile does not exist.");
        var mod = await contents.ImportAsync(source, name, cancellationToken).ConfigureAwait(false);
        var updated = state with { Mods = [.. state.Mods, mod] };
        if (target is not null) updated = updated with
        { Profiles = state.Profiles.Select(p => p.Id == target.Id ? ProfileEditor.Add(p, mod) : p).ToArray() };
        try { await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false); }
        catch { await contents.DeleteAsync(mod.Id, CancellationToken.None).ConfigureAwait(false); throw; }
        return mod;
    }

    public async Task RemoveAsync(Guid modId, CancellationToken cancellationToken = default)
    {
        await using var lease = await store.AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var state = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (state.Mods.All(m => m.Id != modId)) throw new KeyNotFoundException("Mod is not in the library.");
        await store.SaveAsync(state with
        {
            Mods = state.Mods.Where(m => m.Id != modId).ToArray(),
            UpdateChecks = state.UpdateChecks.Where(c => c.ModId != modId).ToArray(),
            Profiles = state.Profiles.Select(p => ProfileEditor.Remove(p, modId)).ToArray()
        }, cancellationToken).ConfigureAwait(false);
        await contents.DeleteAsync(modId, cancellationToken).ConfigureAwait(false);
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
        await store.SaveAsync(state with { Mods = state.Mods.Select(m => m.Id == modId ? m with { Sources = sources.ToArray() } : m).ToArray() }, cancellationToken).ConfigureAwait(false);
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
