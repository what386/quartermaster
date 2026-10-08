using System.Collections.ObjectModel;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public enum ModDeploymentState { Unloaded, Loaded, Warning, Unknown }

public sealed class ProfileModItem(Mod Mod, ProfileEntry Entry, int Index, AsyncCommand EnableCommand, bool HasConflict, string? IconPath = null, ModDeploymentState DeploymentState = ModDeploymentState.Unknown, string WarningDescription = "") : ViewModelBase, IModRow, IProfileListItem
{
    public Mod Mod { get; private set; } = Mod;
    public ProfileEntry Entry { get; private set; } = Entry;
    public int Index { get; private set; } = Index;
    public AsyncCommand EnableCommand { get; private set; } = EnableCommand;
    public bool HasConflict { get; private set; } = HasConflict;
    public string? IconPath { get; private set; } = IconPath;
    public ModDeploymentState DeploymentState { get; private set; } = DeploymentState;
    public string WarningDescription { get; private set; } = WarningDescription;
    public bool HasWarnings => WarningDescription.Length > 0;
    public bool InstalledAsDependency => Mod.InstalledAsDependency;
    public AsyncCommand? UpdateCommand { get; private set; }
    public void Update(ProfileModItem value)
    {
        var changes = new (string Name, object? Before, object? After)[]
        {
            (nameof(Name), Name, value.Name),
            (nameof(Title), Title, value.Title),
            (nameof(Number), Number, value.Number),
            (nameof(Description), Description, value.Description),
            (nameof(Monogram), Monogram, value.Monogram),
            (nameof(HasConflict), HasConflict, value.HasConflict),
            (nameof(WarningDescription), WarningDescription, value.WarningDescription),
            (nameof(HasWarnings), HasWarnings, value.HasWarnings),
            (nameof(InstalledAsDependency), InstalledAsDependency, value.InstalledAsDependency),
            (nameof(IconPath), IconPath, value.IconPath),
            (nameof(IsLoaded), IsLoaded, value.IsLoaded),
            (nameof(IsUnloaded), IsUnloaded, value.IsUnloaded),
            (nameof(HasDeploymentWarning), HasDeploymentWarning, value.HasDeploymentWarning),
            (nameof(DeploymentDescription), DeploymentDescription, value.DeploymentDescription),
            (nameof(HasOptions), HasOptions, value.HasOptions),
            (nameof(IsGrouped), IsGrouped, value.IsGrouped),
            (nameof(IsEnabled), IsEnabled, value.IsEnabled),
            (nameof(KnobAlignment), KnobAlignment, value.KnobAlignment),
            (nameof(ToggleDescription), ToggleDescription, value.ToggleDescription),
            (nameof(Status), Status, value.Status),
            (nameof(HasUpdate), HasUpdate, value.HasUpdate),
            (nameof(UpdateDescription), UpdateDescription, value.UpdateDescription),
        };
        Mod = value.Mod; Entry = value.Entry; Index = value.Index;
        WarningDescription = value.WarningDescription;
        HasConflict = value.HasConflict; IconPath = value.IconPath; DeploymentState = value.DeploymentState;
        // Update commands act on the mod ID, so retain them while an update remains available.
        if (HasUpdate != value.HasUpdate) { UpdateCommand = value.UpdateCommand; Notify(nameof(UpdateCommand)); }
        UpdateDescription = value.UpdateDescription;
        NotifyChanges(changes);
    }

    public void SetUpdate(AsyncCommand? command, string? description) { UpdateCommand = command; UpdateDescription = description; }

    public string? UpdateDescription { get; private set; }
    public bool HasUpdate => UpdateCommand is not null;
    public string Name => Mod.Name;
    public string Title => ModPresentation.Title(Mod);
    public string Number => (Index + 1).ToString();
    public string Description => ModPresentation.Description(Mod);
    public string Monogram => ModPresentation.Monogram(Mod);
    public bool IsLoaded => DeploymentState == ModDeploymentState.Loaded;
    public bool IsUnloaded => DeploymentState == ModDeploymentState.Unloaded;
    public bool HasDeploymentWarning => DeploymentState is ModDeploymentState.Warning or ModDeploymentState.Unknown;
    public string DeploymentDescription => DeploymentState switch
    {
        ModDeploymentState.Unloaded => "Disabled and unloaded",
        ModDeploymentState.Loaded => "Enabled and deployed",
        ModDeploymentState.Unknown => "Deployment state could not be verified. Check the game folder or purge and redeploy.",
        _ => Entry.Enabled ? "Enabled but not deployed as configured. Deploy this profile to apply changes." :
            "Disabled but still deployed. Redeploy or purge to unload."
    };
    public bool HasOptions => Mod.Options.Count > 0;
    public bool HasToggle => true;
    public bool IsGrouped => Entry.GroupId is not null;
    public bool IsEnabled => Entry.Enabled;
    public Avalonia.Layout.HorizontalAlignment KnobAlignment => Entry.Enabled ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
    public string ToggleDescription => (Entry.Enabled ? "Disable " : "Enable ") + Name;
    public string Status => Entry.Enabled ? "Enabled" : "Disabled";
}

public sealed partial class ProfilesViewModel : SessionViewModel
{
    private Profile? profile;
    private ProfileModItem? selectedMod;
    private Mod? modToAdd;
    private string search = "";
    public IReadOnlyList<Profile> Profiles { get; private set; } = [];
    public ObservableCollection<ProfileModItem> Entries { get; } = [];
    public IReadOnlyList<Mod> AvailableMods { get; private set; } = [];
    public IReadOnlyList<string> Conflicts { get; private set; } = [];
    public string ArchiveSummary { get; private set; } = "";
    public IReadOnlyList<ProfileModItem> VisibleEntries => Entries.Where(e => e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string Search { get => search; set { if (Set(ref search, value)) Notify(nameof(VisibleEntries)); RebuildVisibleItems(); } }
    public string EntrySummary => $"{Entries.Count} mods · {Entries.Count(e => e.Entry.Enabled)} on";
    public string SelectedModName => SelectedMod?.Name ?? "Select a mod";
    public bool HasSelectedMod => SelectedMod is not null;
    public bool HasProfile => SelectedProfile is not null;
    public bool HasConflicts => Conflicts.Count > 0;
    public string ActiveLabel => SelectedProfile?.Id == Session.State.ActiveProfileId ? "Active profile" : "";
    public Profile? SelectedProfile
    {
        get => profile;
        set { if (Set(ref profile, value)) { RebuildEntries(); } }
    }
    public ProfileModItem? SelectedMod
    {
        get => selectedMod;
        set
        {
            if (!Set(ref selectedMod, value)) return;
            if (value is not null || selectedListItem is ProfileModItem)
            { selectedListItem = value; Notify(nameof(SelectedListItem)); }
            RefreshSelectedMod();
        }
    }
    private void RefreshSelectedMod()
    {
        Options = SelectedMod is null ? null : new(SelectedMod.Mod, SelectedMod.Entry.Options, Session.GetOptionImages(SelectedMod.Mod));
        Notify(nameof(Options)); Notify(nameof(Details)); Notify(nameof(ToggleLabel)); Notify(nameof(SelectedModName)); Notify(nameof(HasSelectedMod)); RefreshCommands();
    }
    public ModOptionsViewModel? Options { get; private set; }
    public ModDetailsViewModel? Details => SelectedMod is null ? null : new(SelectedMod.Mod, Services, SelectedProfile?.Id);
    public Mod? ModToAdd { get => modToAdd; set { if (Set(ref modToAdd, value) && (!Operations.IsBusy || Operations.IsProgressVisible)) AddCommand.Refresh(); } }
    public string ToggleLabel => SelectedMod?.Entry.Enabled == true ? "Disable" : "Enable";
    public AsyncCommand MakeActiveCommand { get; }
    public AsyncCommand AddCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand ApplyOptionsCommand { get; }
    public AsyncCommand DeployCommand { get; }
    public AsyncCommand RunCommand { get; }
    public AsyncCommand PurgeCommand { get; }
    public AsyncCommand CheckUpdatesCommand { get; }
    public AsyncCommand ImportModCommand { get; }

    public ProfilesViewModel(AppServices services) : base(services)
    {
        CheckUpdatesCommand = Operations.CreateCommand("Checking profile updates", ct => Services.Downloads.CheckUpdatesAsync(ct, Entries.Select(row => row.Mod.Id).ToArray()), () => HasProfile);
        ImportModCommand = Operations.CreateCommand("Adding mod", ct => Services.Downloads.AddAsync(ct, SelectedProfile!.Id), () => HasProfile);
        Services.Downloads.Changed += (_, _) => { foreach (var row in Entries) row.UpdateCommand?.Refresh(); };
        AddGroupCommand = Operations.CreateCommand("Adding group", AddGroupAsync, () => HasProfile);
        MakeActiveCommand = Operations.CreateCommand("Selecting active profile", ct => Session.SaveProfileAsync(SelectedProfile!, true, ct), () => HasProfile);
        AddCommand = Operations.CreateCommand("Adding mod to profile", ct => Services.Downloads.AddLibraryModsToProfileAsync([ModToAdd!.Id], SelectedProfile!.Id, ct), () => HasProfile && ModToAdd is not null);
        RemoveCommand = Operations.CreateCommand("Removing mod from profile", ct => Save(ProfileEditor.Remove(SelectedProfile!, SelectedMod!.Mod.Id), ct), () => SelectedMod is not null);
        ToggleCommand = Operations.CreateCommand("Changing enabled mods", ct => Save(ProfileEditor.SetEnabled(SelectedProfile!, SelectedMod!.Mod.Id, !SelectedMod.Entry.Enabled), ct), () => SelectedMod is not null);
        ApplyOptionsCommand = Operations.CreateCommand("Saving mod options", ct => Save(ProfileEditor.SetOptions(SelectedProfile!, SelectedMod!.Mod, Options!.Selections()), ct), () => Options?.HasOptions == true);
        DeployCommand = Operations.CreateCommand("Deploying profile", async ct =>
        {
            var current = SelectedProfile!;
            var warnings = Entries.Where(row => row.HasWarnings).Select(row => $"• {row.Name}: {row.WarningDescription.Replace("\n", "\n  ")}").ToArray();
            var warningSummary = warnings.Length > 0 ? "\n\nActive warnings:\n" + string.Join("\n", warnings) : "";
            if (await Services.Dialogs.ConfirmAsync("Deploy profile", $"Deploy {current.Name} to {Session.GameDirectory}? This replaces all mod patches in the game folder with the selected loadout." + warningSummary, "Deploy"))
            {
                var names = Session.State.Mods.ToDictionary(mod => mod.Id, mod => mod.Name);
                var progress = Operations.CreateProgress<DeploymentProgress>(update =>
                    $"{update.Phase} {update.Current} of {update.Total}: {names.GetValueOrDefault(update.SourceId, "Unknown mod")}");
                await Session.DeployAsync(current.Id, Services.Dialogs, ct, progress);
            }
        }, () => HasProfile && Session.GameDirectory != "");
        RunCommand = Operations.CreateCommand("Launching game", async ct =>
        {
            var warning = await Session.GetLaunchWarningAsync(SelectedProfile!.Id, ct);
            if (warning is not null && !await Services.Dialogs.ConfirmAsync("Deployment warning", warning, "Run anyway")) return;
            ct.ThrowIfCancellationRequested();
            Services.LaunchGame();
        }, () => HasProfile && Session.GameDirectory != "");
        PurgeCommand = Operations.CreateCommand("Purging patches", async ct =>
        {
            if (await Services.Dialogs.ConfirmAsync("Purge patches", "Remove all mod patch files from the selected game folder? Imported originals and profiles will remain.", "Purge"))
                await Session.PurgeAsync(ct);
        }, () => Session.GameDirectory != "");
        Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationState.IsBusy) || Operations.IsBusy && !Operations.IsProgressVisible) return;
            foreach (var row in Entries) { row.EnableCommand.Refresh(); row.UpdateCommand?.Refresh(); }
            foreach (var group in VisibleItems.OfType<ProfileGroupItem>())
            { group.ToggleCommand.Refresh(); group.RenameCommand.Refresh(); group.RemoveCommand.Refresh(); }
        };
        WatchSession();
    }
    private Task Save(Profile value, CancellationToken ct) => Session.SaveProfileAsync(value, false, ct);
    public Task MoveModAsync(Guid modId, Guid targetModId, bool after) => MoveModsAsync([modId], targetModId, after);
    public Task MoveModsAsync(IReadOnlyCollection<Guid> modIds, Guid targetModId, bool after)
    {
        if (!Operations.CanInteract || SelectedProfile is null || modIds.Count == 0 || modIds.Contains(targetModId)) return Task.CompletedTask;
        var entries = SelectedProfile.Entries;
        var target = entries.ToList().FindIndex(entry => entry.ModId == targetModId);
        if (target < 0) return Task.CompletedTask;
        var moved = ProfileEditor.Move(SelectedProfile, modIds, target + (after ? 1 : 0), entries[target].GroupId);
        if (moved.Entries.SequenceEqual(entries)) return Task.CompletedTask;
        return Operations.RunAsync("Reordering mods", ct => Save(moved, ct), showProgress: false);
    }
    protected override void Refresh()
    {
        Profiles = Session.State.Profiles.ToArray(); Notify(nameof(Profiles));
        // Session refreshes also replace mod metadata, so rebuild once even if the profile is unchanged.
        Set(ref profile, Session.ActiveProfile ?? Profiles.FirstOrDefault(), nameof(SelectedProfile));
        RebuildEntries();
    }
    private void RebuildEntries()
    {
        var selection = SelectedListItem;
        var id = selectedMod?.Mod.Id;
        var existing = Entries.ToDictionary(row => row.Mod.Id);
        var mods = Session.State.Mods.ToDictionary(m => m.Id);
        var report = SelectedProfile is null ? new ConflictReport([], []) : ConflictAnalyzer.Analyze(ProfilePatches.Resolve(Session.State, SelectedProfile));
        var colliding = report.Resources.SelectMany(c => c.SourceIds).ToHashSet();
        var warnings = ModWarnings.ForProfile(Session.State, SelectedProfile, report);
        var rows = SelectedProfile?.Entries.Select((e, index) =>
        {
            // Recycled controls retain their appearance during silent edits; RunAsync still prevents concurrent saves.
            var row = new ProfileModItem(mods[e.ModId], e, index,
            new AsyncCommand(() => Operations.RunAsync("Changing enabled mods", ct => Save(ProfileEditor.SetEnabled(SelectedProfile!, e.ModId, !Entries.Single(row => row.Mod.Id == e.ModId).IsEnabled), ct)),
                () => !Operations.IsProgressVisible, Operations.ReportError), colliding.Contains(e.ModId), Session.GetIconPath(mods[e.ModId]), DeploymentStateFor(mods[e.ModId], e),
                warnings.GetValueOrDefault(e.ModId, ""));
            row.SetUpdate(Services.Downloads.CreateUpdateCommand(mods[e.ModId]), Services.Downloads.UpdateDescription(mods[e.ModId]));
            if (existing.TryGetValue(e.ModId, out var current)) { current.Update(row); return current; }
            return row;
        }).ToArray() ?? [];
        CollectionUpdates.Synchronize(Entries, rows);
        AvailableMods = Session.State.Mods.Where(m => Entries.All(e => e.Mod.Id != m.Id)).ToArray();
        Notify(nameof(VisibleEntries)); Notify(nameof(EntrySummary)); Notify(nameof(AvailableMods));
        RebuildVisibleItems();
        SelectedListItem = selection is ProfileGroupItem group
            ? VisibleItems.OfType<ProfileGroupItem>().FirstOrDefault(item => item.Id == group.Id)
            : VisibleItems.OfType<ProfileModItem>().FirstOrDefault(e => e.Mod.Id == id) ?? VisibleItems.OfType<ProfileModItem>().FirstOrDefault();
        RefreshSelectedMod();
        ModToAdd = AvailableMods.FirstOrDefault();
        Conflicts = report.Resources.Select(c => $"{c.Archive} · {c.Resource.Id:x16}/{c.Resource.Type:x16} · {mods[c.WinningSourceId].Name} wins").ToArray();
        ArchiveSummary = $"{ModPresentation.Count(report.Archives.Count, "shared archive")} · {ModPresentation.Count(Conflicts.Count, "overlapping resource")}";
        Notify(nameof(Conflicts)); Notify(nameof(HasConflicts)); Notify(nameof(ArchiveSummary));
        Notify(nameof(HasProfile)); Notify(nameof(ActiveLabel));
        RefreshCommands();
    }
    private ModDeploymentState DeploymentStateFor(Mod mod, ProfileEntry entry)
    {
        if (Session.Inspection is not { Ledger.Status: DeploymentStatus.Complete } inspection)
            return ModDeploymentState.Unknown;
        var owned = inspection.Ledger.Files.Where(file => file.SourceId == mod.Id).ToArray();
        var health = inspection.Files.ToDictionary(file => file.Name, file => file.Status);
        var anyLoaded = owned.Any(file => health.TryGetValue(file.Name, out var status) && status != ManagedFileStatus.Missing);
        if (!entry.Enabled) return anyLoaded ? ModDeploymentState.Warning : ModDeploymentState.Unloaded;
        if (!anyLoaded) return ModDeploymentState.Warning;
        // Match the selected variants and original hashes; repatched files have their own deployed hashes.
        var expected = PatchSelection.Select(mod, entry).SelectMany(set => set.Files.Select(file => (set.Id, File: file)))
            .ToDictionary(item => (item.Id, item.File.Kind), item => item.File);
        var optionsHash = PatchSelection.OptionsHash(mod, entry);
        return owned.Length == expected.Count && owned.All(file =>
            health.GetValueOrDefault(file.Name, ManagedFileStatus.Missing) == ManagedFileStatus.Present &&
            expected.TryGetValue((file.PatchSetId, file.Kind), out var original) &&
            original.Sha256.Equals(file.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(file.SelectionHash, optionsHash, StringComparison.OrdinalIgnoreCase))
            ? ModDeploymentState.Loaded : ModDeploymentState.Warning;
    }
    private void RefreshCommands()
    {
        if (Operations.IsBusy && !Operations.IsProgressVisible) return;
        foreach (var command in new[] { MakeActiveCommand, AddCommand, RemoveCommand,
            ToggleCommand, ApplyOptionsCommand, DeployCommand, RunCommand, PurgeCommand, AddGroupCommand, CheckUpdatesCommand, ImportModCommand }) command.Refresh();
    }
}
