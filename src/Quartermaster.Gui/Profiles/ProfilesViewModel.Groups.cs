using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Profiles;

public interface IProfileListItem;
public sealed record ProfileGroupItem(ProfileGroup Group, string Summary, bool IsExpanded,
    AsyncCommand ToggleCommand, AsyncCommand RenameCommand, AsyncCommand RemoveCommand) : IProfileListItem
{
    public Guid Id => Group.Id;
    public string Name => Group.Name;
    public string Arrow => IsExpanded ? "▾" : "▸";
}

public sealed partial class ProfilesViewModel
{
    public IReadOnlyList<IProfileListItem> VisibleItems { get; private set; } = [];
    private IProfileListItem? selectedListItem;
    public IProfileListItem? SelectedListItem
    {
        get => selectedListItem;
        set { if (Set(ref selectedListItem, value)) SelectedMod = value as ProfileModItem; }
    }
    public IReadOnlyList<ProfileGroup> Groups => SelectedProfile?.Groups ?? [];
    public AsyncCommand AddGroupCommand { get; }
    public Task CreateGroupFromModsAsync(IReadOnlyCollection<Guid> modIds) => Operations.RunAsync("Adding group", ct => AddGroupAsync(ct, modIds));
    private Task AddGroupAsync(CancellationToken ct) => AddGroupAsync(ct, []);
    private async Task AddGroupAsync(CancellationToken ct, IReadOnlyCollection<Guid> modIds)
    {
        var current = SelectedProfile!;
        var name = await Services.Dialogs.RequestTextAsync("Create group", "Group name", "Create");
        if (name is not null) await Save(ProfileEditor.AddGroup(current, name, modIds), ct);
    }
    private Task ToggleGroupAsync(Guid id) => Operations.RunAsync("Changing group visibility", ct =>
        Save(ProfileEditor.SetGroupExpanded(SelectedProfile!, id, !Groups.Single(group => group.Id == id).IsExpanded), ct), showProgress: false);
    private Task RenameGroupAsync(Guid id) => Operations.RunAsync("Renaming group", async ct =>
    {
        var current = SelectedProfile!; var group = current.Groups.Single(group => group.Id == id);
        var name = await Services.Dialogs.RequestTextAsync("Rename group", "Group name", "Rename", group.Name);
        if (name is not null) await Save(ProfileEditor.RenameGroup(current, id, name), ct);
    });
    private Task RemoveGroupAsync(Guid id) => Operations.RunAsync("Removing group", async ct =>
    {
        var current = SelectedProfile!; var group = current.Groups.Single(group => group.Id == id);
        if (await Services.Dialogs.ConfirmAsync("Remove group", $"Remove {group.Name}? Its mods will remain in this profile and move to the ungrouped section.", "Remove"))
            await Save(ProfileEditor.RemoveGroup(current, id), ct);
    });
    public Task MoveModToGroupAsync(Guid modId, Guid? groupId) => Operations.RunAsync("Moving mod to group", ct =>
        Save(ProfileEditor.SetGroup(SelectedProfile!, modId, groupId), ct));
    public Task MoveGroupAsync(Guid groupId, Guid targetId, bool after)
    {
        if (!Operations.CanInteract || SelectedProfile is null || groupId == targetId) return Task.CompletedTask;
        var groups = Groups.ToList();
        var from = groups.FindIndex(group => group.Id == groupId);
        var target = groups.FindIndex(group => group.Id == targetId);
        if (from < 0 || target < 0) return Task.CompletedTask;
        var destination = target + (after ? 1 : 0);
        if (from < destination) destination--;
        if (from == destination) return Task.CompletedTask;
        var moved = ProfileEditor.MoveGroup(SelectedProfile, groupId, destination);
        return Operations.RunAsync("Reordering groups", ct => Save(moved, ct), showProgress: false);
    }
    private void RebuildVisibleItems()
    {
        var items = new List<IProfileListItem>();
        items.AddRange(Entries.Where(entry => entry.Entry.GroupId is null && entry.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)));
        foreach (var group in Groups)
        {
            var members = Entries.Where(entry => entry.Entry.GroupId == group.Id).ToArray();
            var matching = members.Where(entry => entry.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) ||
                group.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (Search != "" && matching.Length == 0 && !group.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var expanded = group.IsExpanded || Search != "";
            items.Add(new ProfileGroupItem(group, $"{members.Length} mods · {members.Count(entry => entry.IsEnabled)} on", expanded,
                new AsyncCommand(() => ToggleGroupAsync(group.Id), () => Operations.CanInteract && Search == "", Operations.ReportError),
                new AsyncCommand(() => RenameGroupAsync(group.Id), () => Operations.CanInteract, Operations.ReportError),
                new AsyncCommand(() => RemoveGroupAsync(group.Id), () => Operations.CanInteract, Operations.ReportError)));
            if (expanded) items.AddRange(matching);
        }
        VisibleItems = items.ToArray(); Notify(nameof(VisibleItems)); Notify(nameof(Groups));
    }
}
