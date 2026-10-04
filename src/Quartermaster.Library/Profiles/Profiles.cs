using Quartermaster.Core.Patching;
using Quartermaster.Library.Mods;

namespace Quartermaster.Library.Profiles;

public enum PriorityDirection { LastWins, FirstWins }
public sealed record ProfileEntry(Guid ModId, bool Enabled, IReadOnlyList<OptionSelection> Options)
{
    public Guid? GroupId { get; init; }
}
public sealed record ProfileGroup(Guid Id, string Name, bool IsExpanded = true);
public sealed record Profile(Guid Id, string Name, PriorityDirection Priority, IReadOnlyList<ProfileEntry> Entries)
{
    public IReadOnlyList<ProfileGroup> Groups { get; init; } = [];
}

public static class ProfileEditor
{
    public static Profile Create(string name) => new(Guid.NewGuid(), Name(name), PriorityDirection.LastWins, []);
    public static Profile Add(Profile profile, Mod mod)
    {
        if (profile.Entries.Any(e => e.ModId == mod.Id)) throw new ArgumentException("Mod is already in the profile.");
        return Organize(profile with { Entries = [.. profile.Entries, new(mod.Id, true, [])] });
    }
    public static Profile Remove(Profile profile, Guid modId) => profile with
    { Entries = profile.Entries.Where(e => e.ModId != modId).ToArray() };
    public static Profile SetEnabled(Profile profile, Guid modId, bool enabled) => Change(profile, modId, e => e with { Enabled = enabled });
    public static Profile SetOptions(Profile profile, Mod mod, IReadOnlyList<OptionSelection> options)
    {
        PatchSelection.Select(mod, new(mod.Id, true, options));
        return Change(profile, mod.Id, e => e with { Options = options.ToArray() });
    }
    public static Profile AddGroup(Profile profile, string name, IReadOnlyCollection<Guid>? modIds = null)
    {
        var selected = modIds?.ToHashSet() ?? [];
        if (selected.Any(id => profile.Entries.All(entry => entry.ModId != id)))
            throw new KeyNotFoundException("Mod is not in the profile.");
        var group = new ProfileGroup(Guid.NewGuid(), Name(name));
        return Organize(profile with
        {
            Groups = [.. profile.Groups, group],
            Entries = profile.Entries.Select(entry => selected.Contains(entry.ModId) ? entry with { GroupId = group.Id } : entry).ToArray()
        });
    }
    public static Profile MoveGroup(Profile profile, Guid groupId, int index)
    {
        RequireGroup(profile, groupId);
        if (index < 0 || index >= profile.Groups.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var groups = profile.Groups.ToList();
        var group = groups.Single(item => item.Id == groupId);
        groups.Remove(group); groups.Insert(index, group);
        return Organize(profile with { Groups = groups.ToArray() });
    }
    public static Profile RenameGroup(Profile profile, Guid groupId, string name) => ChangeGroup(profile, groupId, group => group with { Name = Name(name) });
    public static Profile SetGroupExpanded(Profile profile, Guid groupId, bool expanded) => ChangeGroup(profile, groupId, group => group with { IsExpanded = expanded });
    public static Profile RemoveGroup(Profile profile, Guid groupId)
    {
        RequireGroup(profile, groupId);
        return Organize(profile with
        {
            Groups = profile.Groups.Where(group => group.Id != groupId).ToArray(),
            Entries = profile.Entries.Select(entry => entry.GroupId == groupId ? entry with { GroupId = null } : entry).ToArray()
        });
    }
    public static Profile SetGroup(Profile profile, Guid modId, Guid? groupId)
    {
        RequireGroup(profile, groupId);
        if (profile.Entries.All(entry => entry.ModId != modId)) throw new KeyNotFoundException("Mod is not in the profile.");
        var entry = profile.Entries.Single(entry => entry.ModId == modId);
        if (entry.GroupId == groupId) return profile;
        return Organize(profile with { Entries = [.. profile.Entries.Where(item => item.ModId != modId), entry with { GroupId = groupId }] });
    }
    /// <summary>Moves a selection to an insertion boundary in the original entry order.</summary>
    public static Profile Move(Profile profile, IReadOnlyCollection<Guid> modIds, int boundary, Guid? groupId)
    {
        RequireGroup(profile, groupId);
        if (boundary < 0 || boundary > profile.Entries.Count) throw new ArgumentOutOfRangeException(nameof(boundary));
        var selected = modIds.ToHashSet();
        if (selected.Any(id => profile.Entries.All(entry => entry.ModId != id))) throw new KeyNotFoundException("Mod is not in the profile.");
        if (selected.Count == 0) return profile;
        var moving = profile.Entries.Where(entry => selected.Contains(entry.ModId)).Select(entry => entry with { GroupId = groupId }).ToArray();
        var insertion = boundary - profile.Entries.Take(boundary).Count(entry => selected.Contains(entry.ModId));
        var remaining = profile.Entries.Where(entry => !selected.Contains(entry.ModId)).ToList();
        remaining.InsertRange(insertion, moving);
        return Organize(profile with { Entries = remaining.ToArray() });
    }
    public static Profile Move(Profile profile, Guid modId, int index) => Move(profile, modId, index,
        profile.Entries.SingleOrDefault(entry => entry.ModId == modId)?.GroupId);
    public static Profile Move(Profile profile, Guid modId, int index, Guid? groupId)
    {
        RequireGroup(profile, groupId);
        if (index < 0 || index >= profile.Entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var entries = profile.Entries.ToList();
        var old = entries.FindIndex(e => e.ModId == modId);
        if (old < 0) throw new KeyNotFoundException("Mod is not in the profile.");
        var entry = entries[old] with { GroupId = groupId }; entries.RemoveAt(old); entries.Insert(index, entry);
        return Organize(profile with { Entries = entries.ToArray() });
    }
    private static Profile Organize(Profile profile)
    {
        var order = profile.Groups.Select((group, index) => (group.Id, index)).ToDictionary(item => item.Id, item => item.index);
        return profile with { Entries = profile.Entries.OrderBy(entry => entry.GroupId is { } id ? order[id] : -1).ToArray() };
    }
    private static void RequireGroup(Profile profile, Guid? groupId)
    {
        if (groupId is { } id && profile.Groups.All(group => group.Id != id)) throw new KeyNotFoundException("Group is not in the profile.");
    }
    private static Profile ChangeGroup(Profile profile, Guid groupId, Func<ProfileGroup, ProfileGroup> change)
    {
        RequireGroup(profile, groupId);
        return profile with { Groups = profile.Groups.Select(group => group.Id == groupId ? change(group) : group).ToArray() };
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
    private static string Name(string name) => !string.IsNullOrWhiteSpace(name) ? name.Trim() : throw new ArgumentException("A name is required.");
}

public sealed record OptionChoice(string Name, IReadOnlyList<Guid> PatchSetIds);
public sealed record ModOption(Guid Id, string Name, string Description,
    IReadOnlyList<Guid> PatchSetIds, IReadOnlyList<OptionChoice> Choices);
public sealed record OptionSelection(Guid OptionId, bool Enabled = true, int ChoiceIndex = 0);

public static class PatchSelection
{
    public static string? OptionsHash(Mod mod, ProfileEntry entry)
    {
        if (entry.ModId != mod.Id) throw new ArgumentException("Entry belongs to another mod.");
        if (mod.Options.Count == 0) return null;
        var selections = entry.Options.ToDictionary(option => option.OptionId);
        var normalized = mod.Options.OrderBy(option => option.Id)
            .Select(option => selections.GetValueOrDefault(option.Id) ?? new OptionSelection(option.Id)).ToArray();
        return Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(
            global::System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(normalized))).ToLowerInvariant();
    }
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
