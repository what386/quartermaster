using System.Collections.ObjectModel;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Gui.Profiles;

public interface IProfileListItem;
public sealed class ProfileGroupItem(ProfileGroup Group, string Summary, bool IsExpanded,
    AsyncCommand ToggleCommand, AsyncCommand RenameCommand, AsyncCommand RemoveCommand, AsyncCommand ColorsCommand) : ViewModelBase, IProfileListItem
{
    public ProfileGroup Group { get; private set; } = Group;
    public string Summary { get; private set; } = Summary;
    public bool IsExpanded { get; private set; } = IsExpanded;
    public AsyncCommand ToggleCommand { get; } = ToggleCommand;
    public AsyncCommand RenameCommand { get; } = RenameCommand;
    public AsyncCommand RemoveCommand { get; } = RemoveCommand;
    public AsyncCommand ColorsCommand { get; } = ColorsCommand;
    public string? BackgroundColor => Group.BackgroundColor;
    public string? TextColor => Group.TextColor;
    public void Update(ProfileGroupItem value)
    {
        var changes = new (string Name, object? Before, object? After)[]
        {
            (nameof(Name), Name, value.Name),
            (nameof(BackgroundColor), BackgroundColor, value.BackgroundColor),
            (nameof(TextColor), TextColor, value.TextColor),
            (nameof(Summary), Summary, value.Summary),
            (nameof(IsExpanded), IsExpanded, value.IsExpanded),
            (nameof(Arrow), Arrow, value.Arrow)
        };
        Group = value.Group; Summary = value.Summary; IsExpanded = value.IsExpanded;
        NotifyChanges(changes);
    }
    public Guid Id => Group.Id;
    public string Name => Group.Name;
    public string Arrow => IsExpanded ? "▾" : "▸";
}

public sealed partial class ProfilesViewModel
{
    public ObservableCollection<IProfileListItem> VisibleItems { get; } = [];
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
        var name = await Services.Dialogs.RequestTextAsync(Localizer.Text("Create group"), Localizer.Text("Group name"), Localizer.Text("Create"));
        if (name is not null) await Save(ProfileEditor.AddGroup(current, name, modIds), ct);
    }
    private Task ToggleGroupAsync(Guid id) => Operations.RunAsync("Changing group visibility", ct =>
        Save(ProfileEditor.SetGroupExpanded(SelectedProfile!, id, !Groups.Single(group => group.Id == id).IsExpanded), ct), showProgress: false);
    private Task RenameGroupAsync(Guid id) => Operations.RunAsync("Renaming group", async ct =>
    {
        var current = SelectedProfile!; var group = current.Groups.Single(group => group.Id == id);
        var name = await Services.Dialogs.RequestTextAsync(Localizer.Text("Rename group"), Localizer.Text("Group name"), Localizer.Text("Rename"), group.Name);
        if (name is not null) await Save(ProfileEditor.RenameGroup(current, id, name), ct);
    });
    private Task ChangeGroupColorsAsync(Guid id) => Operations.RunAsync("Changing group colors", async ct =>
    {
        var current = SelectedProfile!; var group = current.Groups.Single(group => group.Id == id);
        var colors = await Services.Dialogs.RequestGroupColorsAsync(group.BackgroundColor, group.TextColor);
        if (colors is not null) await Save(ProfileEditor.SetGroupColors(current, id, colors.Background, colors.Text), ct);
    }, showProgress: false);
    private Task RemoveGroupAsync(Guid id) => Operations.RunAsync("Removing group", async ct =>
    {
        var current = SelectedProfile!; var group = current.Groups.Single(group => group.Id == id);
        if (await Services.Dialogs.ConfirmAsync(Localizer.Text("Remove group"), Localizer.Interpolate($"Remove {group.Name}? Its mods will remain in this profile and move to the ungrouped section."), Localizer.Text("Remove")))
            await Save(ProfileEditor.RemoveGroup(current, id), ct);
    });
    public Task MoveModToGroupAsync(Guid modId, Guid? groupId) => MoveModsToGroupAsync([modId], groupId);
    public Task MoveModsToGroupAsync(IReadOnlyCollection<Guid> modIds, Guid? groupId)
    {
        if (!Operations.CanInteract || SelectedProfile is null || modIds.Count == 0) return Task.CompletedTask;
        var moved = ProfileEditor.Move(SelectedProfile, modIds, SelectedProfile.Entries.Count, groupId);
        if (moved.Entries.SequenceEqual(SelectedProfile.Entries)) return Task.CompletedTask;
        return Operations.RunAsync("Moving mods to group", ct => Save(moved, ct), showProgress: false);
    }
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
        var existing = VisibleItems.OfType<ProfileGroupItem>().ToDictionary(row => row.Id);
        var items = new List<IProfileListItem>();
        items.AddRange(Entries.Where(entry => entry.Entry.GroupId is null && Library.Mods.ModTags.Matches(entry.Mod, Search)));
        foreach (var group in Groups)
        {
            var members = Entries.Where(entry => entry.Entry.GroupId == group.Id).ToArray();
            var matching = members.Where(entry => Library.Mods.ModTags.Matches(entry.Mod, Search) ||
                group.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (Search != "" && matching.Length == 0 && !group.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            var expanded = group.IsExpanded || Search != "";
            var row = new ProfileGroupItem(group, Localizer.Interpolate($"{members.Length} mods · {members.Count(entry => entry.IsEnabled)} on"), expanded,
                new AsyncCommand(() => ToggleGroupAsync(group.Id), () => !Operations.IsProgressVisible && Search == "", Operations.ReportError),
                new AsyncCommand(() => RenameGroupAsync(group.Id), () => !Operations.IsProgressVisible, Operations.ReportError),
                new AsyncCommand(() => RemoveGroupAsync(group.Id), () => !Operations.IsProgressVisible, Operations.ReportError),
                new AsyncCommand(() => ChangeGroupColorsAsync(group.Id), () => !Operations.IsProgressVisible, Operations.ReportError));
            if (existing.TryGetValue(group.Id, out var current)) { current.Update(row); row = current; }
            items.Add(row);
            if (expanded) items.AddRange(matching);
        }
        CollectionUpdates.Synchronize(VisibleItems, items); Notify(nameof(Groups));
        if (!Operations.IsBusy)
            foreach (var group in VisibleItems.OfType<ProfileGroupItem>()) group.ToggleCommand.Refresh();
    }
}
