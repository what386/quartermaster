using Quartermaster.Core.Patching;
using static Quartermaster.Core.Patching.PatchValidation;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public static class StateValidation
{
    public static void Validate(LibraryState state)
    {
        if (state.SchemaVersion != LibraryState.CurrentSchemaVersion) throw new ArgumentException("Unsupported library schema version.");
        Unique(state.Mods.Select(m => m.Id)); Unique(state.Profiles.Select(p => p.Id));
        foreach (var mod in state.Mods)
        {
            if (string.IsNullOrWhiteSpace(mod.Name) || mod.PatchSets.Count == 0) throw new ArgumentException("Invalid mod metadata.");
            Unique(mod.PatchSets.Select(p => p.Id)); Unique(mod.Options.Select(o => o.Id));
            if (mod.Sources.Any(s => string.IsNullOrWhiteSpace(s.Provider) || string.IsNullOrWhiteSpace(s.ModId)))
                throw new ArgumentException("Invalid provider reference.");
            foreach (var set in mod.PatchSets)
            {
                if (!IsArchive(set.Archive) || set.OriginalSlot < 0 || (set.Folder != "" && !IsRelativePath(set.Folder)))
                    throw new ArgumentException("Invalid patch-set metadata.");
                ValidateFiles(set.Files);
            }
            foreach (var option in mod.Options)
                if (string.IsNullOrWhiteSpace(option.Name) || option.Choices.Any(c => string.IsNullOrWhiteSpace(c.Name)) ||
                    option.PatchSetIds.Concat(option.Choices.SelectMany(c => c.PatchSetIds)).Any(id => mod.PatchSets.All(s => s.Id != id)))
                    throw new ArgumentException("Option references a missing patch set.");
        }
        foreach (var profile in state.Profiles)
        {
            Unique(profile.Entries.Select(e => e.ModId));
            if (string.IsNullOrWhiteSpace(profile.Name) || !Enum.IsDefined(profile.Priority)) throw new ArgumentException("Invalid profile metadata.");
            foreach (var entry in profile.Entries)
            {
                var mod = state.Mods.SingleOrDefault(m => m.Id == entry.ModId) ?? throw new ArgumentException("Profile references a missing mod.");
                PatchSelection.Select(mod, entry with { Enabled = true });
            }
        }
        if (state.ActiveProfileId is { } active && state.Profiles.All(p => p.Id != active)) throw new ArgumentException("Active profile does not exist.");
        if (state.UpdateChecks.Any(c => state.Mods.All(m => m.Id != c.ModId) || string.IsNullOrWhiteSpace(c.Provider)))
            throw new ArgumentException("Update check references an unknown mod or provider.");
    }
    private static void Unique(IEnumerable<Guid> ids)
    {
        var seen = new HashSet<Guid>();
        if (ids.Any(id => id == Guid.Empty || !seen.Add(id))) throw new ArgumentException("IDs must be nonempty and unique.");
    }
}
