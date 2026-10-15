using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Interop.Echelon;

/// <summary>Copies all referenced mod variants, then commits complete profiles under the library lock.</summary>
public sealed class EchelonImporter(ILibraryStore store, IModContentStore contents)
{
    public async Task<EchelonImportResult> ImportAsync(EchelonImportPlan plan, IProgress<EchelonImportProgress>? progress = null, CancellationToken ct = default)
    {
        await using var lease = await store.AcquireLockAsync(ct).ConfigureAwait(false);
        var original = await store.LoadAsync(ct).ConfigureAwait(false);
        var mods = original.Mods.ToList();
        var created = new HashSet<Guid>();
        var mapping = new Dictionary<string, Mod>(StringComparer.Ordinal);
        var commitStarted = false;
        var reused = 0;
        try
        {
            foreach (var record in plan.Mods)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(mapping.Count + 1, plan.Mods.Count, record.Name));
                var imported = await EchelonContent.ImportAsync(contents, record, ct).ConfigureAwait(false);
                created.Add(imported.Id);
                imported = imported with
                {
                    Name = record.Name,
                    Description = record.Description,
                    PageLink = record.PageLink,
                    Tags = ModTags.Normalize(record.Tags.Concat(imported.Tags)),
                    ImportedAt = record.AddedAt ?? imported.ImportedAt,
                    Version = record.Version ?? imported.Version ?? record.Source?.InstalledVersion,
                    Sources = record.Source is { } source ? [source with { InstalledVersion = source.InstalledVersion ?? imported.Version }] : imported.Sources
                };
                var identity = ModIdentity.GetKey(imported);
                var existing = mods.FirstOrDefault(mod => ModIdentity.GetKey(mod) == identity);
                if (existing is not null)
                {
                    existing = existing with { Tags = ModTags.Normalize(existing.Tags.Concat(imported.Tags)) };
                    mods[mods.FindIndex(mod => mod.Id == existing.Id)] = existing;
                    await contents.DeleteAsync(imported.Id, CancellationToken.None).ConfigureAwait(false);
                    created.Remove(imported.Id);
                    mapping.Add(record.Id, existing);
                    reused++;
                }
                else
                {
                    mods.Add(imported);
                    mapping.Add(record.Id, imported);
                }
            }
            var profiles = original.Profiles.ToList();
            var importedProfiles = new List<Profile>();
            var byKey = new Dictionary<string, Profile>(StringComparer.Ordinal);
            var addedProfiles = 0;
            foreach (var source in plan.Profiles)
            {
                ct.ThrowIfCancellationRequested();
                var groups = new List<ProfileGroup>();
                var entries = new List<ProfileEntry>();
                Guid? groupId = null;
                foreach (var row in source.Rows)
                {
                    if (row.GroupName is { } label)
                    {
                        var group = new ProfileGroup(Guid.NewGuid(), label)
                        { BackgroundColor = row.BackgroundColor, TextColor = row.TextColor };
                        groups.Add(group); groupId = group.Id;
                        continue;
                    }
                    if (!mapping.TryGetValue(row.Id, out var mod)) throw new InvalidDataException($"Echelon profile references a missing imported mod: {row.Id}");
                    entries.Add(new(mod.Id, row.Enabled, SelectOptions(mod, row.Options)) { GroupId = groupId });
                }
                var profile = new Profile(Guid.NewGuid(), source.Name, source.Priority, entries) { Groups = groups, Thumbnail = source.Thumbnail };
                var existing = profiles.FirstOrDefault(candidate => Equivalent(candidate, profile));
                if (existing is not null) profile = existing;
                else
                {
                    profile = profile with { Name = UniqueName(profile.Name, profiles) };
                    profiles.Add(profile); addedProfiles++;
                }
                importedProfiles.Add(profile); byKey.Add(source.Key, profile);
            }
            if (importedProfiles.Count == 0) throw new InvalidDataException("No Echelon profiles were selected.");
            var active = plan.SelectedProfile is { } selected && byKey.TryGetValue(selected, out var selectedProfile)
                ? selectedProfile.Id : importedProfiles[0].Id;
            var updated = original with { Mods = mods.ToArray(), Profiles = profiles.ToArray(), ActiveProfileId = active };
            StateValidation.Validate(updated);
            ct.ThrowIfCancellationRequested();
            commitStarted = true;
            await store.SaveAsync(updated, ct).ConfigureAwait(false);
            return new(importedProfiles, created.Count, reused, addedProfiles, active);
        }
        catch (Exception importError)
        {
            // A store can write multiple documents. Restore metadata before removing staged mod files.
            if (commitStarted)
            {
                try { await store.SaveAsync(original, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception restoreError)
                { throw new AggregateException("Echelon import failed and library metadata could not be restored. Imported files were retained for recovery.", importError, restoreError); }
            }
            foreach (var id in created) await contents.DeleteAsync(id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyList<OptionSelection> SelectOptions(Mod mod, IReadOnlyList<EchelonOptionSelection> configuration)
    {
        if (configuration.Count != mod.Options.Count) throw new InvalidDataException($"Echelon options no longer match the mod files: {mod.Name}");
        return mod.Options.Select((option, index) => new OptionSelection(option.Id, configuration[index].Enabled, configuration[index].ChoiceIndex)).ToArray();
    }
    private static bool Equivalent(Profile left, Profile right)
    {
        if (left.Name != right.Name && !left.Name.StartsWith(right.Name + " (Echelon", StringComparison.Ordinal)) return false;
        if (left.Thumbnail != right.Thumbnail || left.Priority != right.Priority ||
            !left.Groups.Select(group => (group.Name, group.BackgroundColor, group.TextColor)).SequenceEqual(right.Groups.Select(group => (group.Name, group.BackgroundColor, group.TextColor))) || left.Entries.Count != right.Entries.Count) return false;
        for (var index = 0; index < left.Entries.Count; index++)
        {
            var a = left.Entries[index]; var b = right.Entries[index];
            if (a.ModId != b.ModId || a.Enabled != b.Enabled || !a.Options.SequenceEqual(b.Options) ||
                GroupIndex(left, a.GroupId) != GroupIndex(right, b.GroupId)) return false;
        }
        return true;
    }
    private static int GroupIndex(Profile profile, Guid? id) => id is null ? -1 : profile.Groups.ToList().FindIndex(group => group.Id == id);
    private static string UniqueName(string name, IReadOnlyList<Profile> profiles)
    {
        if (profiles.All(profile => profile.Name != name)) return name;
        var candidate = name + " (Echelon)";
        for (var index = 2; profiles.Any(profile => profile.Name == candidate); index++) candidate = name + $" (Echelon {index})";
        return candidate;
    }
}
