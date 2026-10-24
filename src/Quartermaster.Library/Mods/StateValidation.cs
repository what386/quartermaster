using Quartermaster.Core.Patching;
using static Quartermaster.Core.Patching.PatchValidation;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Library.Mods;

public static class StateValidation
{
    public static void Validate(LibraryState state)
    {
        if (state.SchemaVersion != LibraryState.CurrentSchemaVersion) throw new ArgumentException("Unsupported library schema version.");
        var invalidMods = state.Mods.GroupBy(mod => mod.Id).Where(group => group.Key == Guid.Empty || group.Count() > 1)
            .SelectMany(group => group).ToArray();
        if (invalidMods.Length > 0) throw new ArgumentException($"Invalid or duplicate mod IDs: {string.Join(", ", invalidMods.Select(mod => $"{mod.Name} ({mod.Id})"))}");
        Unique(state.Profiles.Select(p => p.Id));
        foreach (var mod in state.Mods)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(mod.Name) || mod.PatchSets.Count == 0) throw new ArgumentException("Invalid mod metadata.");
                ModLinks.ValidatePage(mod.PageLink);
                ModTags.Normalize(mod.Tags);
                if (mod.ImportedFileName is { } filename && (string.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(['/', '\\']) >= 0))
                    throw new ArgumentException("Invalid imported filename.");
                Unique(mod.PatchSets.Select(p => p.Id)); Unique(mod.Options.Select(o => o.Id));
                if (mod.Sources.Any(s => string.IsNullOrWhiteSpace(s.Provider) || string.IsNullOrWhiteSpace(s.ModId)))
                    throw new ArgumentException("Invalid provider reference.");
                foreach (var set in mod.PatchSets)
                {
                    if (!IsArchive(set.Archive) || set.OriginalSlot < 0 || !Enum.IsDefined(set.ContentKind) || set.FileModPayloadIndex is < 0 || (set.Folder != "" && !IsRelativePath(set.Folder)))
                        throw new ArgumentException("Invalid patch-set metadata.");
                    ValidateFiles(set.Files);
                }
                foreach (var option in mod.Options)
                    if (string.IsNullOrWhiteSpace(option.Name) || option.Choices.Any(c => string.IsNullOrWhiteSpace(c.Name)) ||
                        option.PatchSetIds.Concat(option.Choices.SelectMany(c => c.PatchSetIds)).Any(id => mod.PatchSets.All(s => s.Id != id)))
                        throw new ArgumentException("Option references a missing patch set.");
            }
            catch (ArgumentException ex)
            { throw new ArgumentException($"Invalid data for mod '{mod.Name}' ({mod.Id}): {ex.Message}", ex); }
        }
        foreach (var profile in state.Profiles)
        {
            if (profile.Description is null || profile.Description.Length > ProfileEditor.MaxDescriptionLength)
                throw new ArgumentException("Invalid profile description.");
            ProfileAppearance.ValidateThumbnail(profile.Thumbnail);
            foreach (var group in profile.Groups)
            { ProfileAppearance.NormalizeColor(group.BackgroundColor); ProfileAppearance.NormalizeColor(group.TextColor); }
            Unique(profile.Entries.Select(e => e.ModId)); Unique(profile.Groups.Select(group => group.Id));
            if (profile.Groups.Any(group => string.IsNullOrWhiteSpace(group.Name))) throw new ArgumentException("Invalid group metadata.");
            var groupOrder = profile.Groups.Select((group, index) => (group.Id, index)).ToDictionary(item => item.Id, item => item.index);
            foreach (var entry in profile.Entries.Where(entry => entry.GroupId is { } id && !groupOrder.ContainsKey(id)))
                throw new ArgumentException($"Mod '{state.Mods.FirstOrDefault(mod => mod.Id == entry.ModId)?.Name ?? entry.ModId.ToString()}' in profile '{profile.Name}' references missing group {entry.GroupId}.");
            var positions = profile.Entries.Select(entry => entry.GroupId is { } id ? groupOrder[id] : -1).ToArray();
            if (!positions.SequenceEqual(positions.Order())) throw new ArgumentException("Profile groups do not match load order.");
            if (string.IsNullOrWhiteSpace(profile.Name) || !Enum.IsDefined(profile.Priority)) throw new ArgumentException("Invalid profile metadata.");
            foreach (var entry in profile.Entries)
            {
                var mod = state.Mods.SingleOrDefault(m => m.Id == entry.ModId) ?? throw new ArgumentException($"Profile '{profile.Name}' references missing mod {entry.ModId}.");
                PatchSelection.Select(mod, entry with { Enabled = true });
            }
        }
        if (state.ActiveProfileId is { } active && state.Profiles.All(p => p.Id != active)) throw new ArgumentException("Active profile does not exist.");
        foreach (var check in state.UpdateChecks.Where(c => state.Mods.All(m => m.Id != c.ModId) || string.IsNullOrWhiteSpace(c.Provider)))
            throw new ArgumentException($"Update check references unknown mod {check.ModId} or invalid provider '{check.Provider}'.");
    }
    private static void Unique(IEnumerable<Guid> ids)
    {
        var seen = new HashSet<Guid>();
        if (ids.Any(id => id == Guid.Empty || !seen.Add(id))) throw new ArgumentException("IDs must be nonempty and unique.");
    }
}
