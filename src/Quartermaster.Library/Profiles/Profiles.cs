using Quartermaster.Core.Patching;
using Quartermaster.Library.Mods;

namespace Quartermaster.Library.Profiles;

public enum PriorityDirection { LastWins, FirstWins }
public sealed record ProfileEntry(Guid ModId, bool Enabled, IReadOnlyList<OptionSelection> Options);
public sealed record Profile(Guid Id, string Name, PriorityDirection Priority, IReadOnlyList<ProfileEntry> Entries);

public static class ProfileEditor
{
    public static Profile Create(string name) => new(Guid.NewGuid(), Name(name), PriorityDirection.LastWins, []);
    public static Profile Add(Profile profile, Mod mod)
    {
        if (profile.Entries.Any(e => e.ModId == mod.Id)) throw new ArgumentException("Mod is already in the profile.");
        return profile with { Entries = [.. profile.Entries, new(mod.Id, true, [])] };
    }
    public static Profile Remove(Profile profile, Guid modId) => profile with
    { Entries = profile.Entries.Where(e => e.ModId != modId).ToArray() };
    public static Profile SetEnabled(Profile profile, Guid modId, bool enabled) => Change(profile, modId, e => e with { Enabled = enabled });
    public static Profile SetOptions(Profile profile, Mod mod, IReadOnlyList<OptionSelection> options)
    {
        PatchSelection.Select(mod, new(mod.Id, true, options));
        return Change(profile, mod.Id, e => e with { Options = options.ToArray() });
    }
    public static Profile Move(Profile profile, Guid modId, int index)
    {
        if (index < 0 || index >= profile.Entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var entries = profile.Entries.ToList();
        var old = entries.FindIndex(e => e.ModId == modId);
        if (old < 0) throw new KeyNotFoundException("Mod is not in the profile.");
        var entry = entries[old]; entries.RemoveAt(old); entries.Insert(index, entry);
        return profile with { Entries = entries.ToArray() };
    }
    public static IEnumerable<ProfileEntry> InDeploymentOrder(Profile profile) => profile.Priority switch
    {
        PriorityDirection.LastWins => profile.Entries,
        PriorityDirection.FirstWins => profile.Entries.Reverse(),
        _ => throw new ArgumentException("Unknown priority direction.")
    };
    private static Profile Change(Profile profile, Guid modId, Func<ProfileEntry, ProfileEntry> change)
    {
        if (profile.Entries.All(e => e.ModId != modId)) throw new KeyNotFoundException("Mod is not in the profile.");
        return profile with { Entries = profile.Entries.Select(e => e.ModId == modId ? change(e) : e).ToArray() };
    }
    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) ? name.Trim() : throw new ArgumentException("Profile name is required.");
}

public sealed record OptionChoice(string Name, IReadOnlyList<Guid> PatchSetIds);
public sealed record ModOption(Guid Id, string Name, string Description,
    IReadOnlyList<Guid> PatchSetIds, IReadOnlyList<OptionChoice> Choices);
public sealed record OptionSelection(Guid OptionId, bool Enabled = true, int ChoiceIndex = 0);

public static class PatchSelection
{
    public static IReadOnlyList<PatchSet> Select(Mod mod, ProfileEntry entry)
    {
        if (entry.ModId != mod.Id) throw new ArgumentException("Entry belongs to another mod.");
        if (!entry.Enabled) return [];
        var selections = entry.Options.ToDictionary(o => o.OptionId);
        if (selections.Keys.Any(id => mod.Options.All(o => o.Id != id)))
            throw new ArgumentException("Selection refers to an unknown option.");
        var assigned = mod.Options.SelectMany(o => o.PatchSetIds.Concat(o.Choices.SelectMany(c => c.PatchSetIds))).ToHashSet();
        var selected = new List<Guid>();
        var seen = new HashSet<Guid>();
        void Add(Guid id) { if (seen.Add(id)) selected.Add(id); }
        foreach (var set in mod.PatchSets.Where(p => !assigned.Contains(p.Id))) Add(set.Id);
        foreach (var option in mod.Options)
        {
            var selection = selections.GetValueOrDefault(option.Id) ?? new OptionSelection(option.Id);
            if (!selection.Enabled) continue;
            foreach (var id in option.PatchSetIds) Add(id);
            if (option.Choices.Count == 0) continue;
            if (selection.ChoiceIndex < 0 || selection.ChoiceIndex >= option.Choices.Count)
                throw new ArgumentException($"Invalid choice for option {option.Name}.");
            foreach (var id in option.Choices[selection.ChoiceIndex].PatchSetIds) Add(id);
        }
        if (selected.Any(id => mod.PatchSets.All(p => p.Id != id)))
            throw new ArgumentException("Option refers to an unknown patch set.");
        var sets = mod.PatchSets.ToDictionary(p => p.Id);
        return selected.Select(id => sets[id]).ToArray();
    }
}
