using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public sealed record ProfileModItem(Mod Mod, ProfileEntry Entry, int Index, AsyncCommand EnableCommand, bool HasConflict, string? IconPath = null) : IModRow
{
    public string Name => Mod.Name;
    public string Title => ModPresentation.Title(Mod);
    public string Number => (Index + 1).ToString();
    public string Description => ModPresentation.Description(Mod);
    public string Monogram => ModPresentation.Monogram(Mod);
    public bool HasToggle => true;
    public bool IsEnabled => Entry.Enabled;
    public Avalonia.Layout.HorizontalAlignment KnobAlignment => Entry.Enabled ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
    public string ToggleDescription => (Entry.Enabled ? "Disable " : "Enable ") + Name;
    public string Status => Entry.Enabled ? "Enabled" : "Disabled";
}

public sealed class ProfilesViewModel : SessionViewModel
{
    private Profile? profile;
    private ProfileModItem? selectedMod;
    private Mod? modToAdd;
    private string search = "";
    public IReadOnlyList<Profile> Profiles { get; private set; } = [];
    public IReadOnlyList<ProfileModItem> Entries { get; private set; } = [];
    public IReadOnlyList<Mod> AvailableMods { get; private set; } = [];
    public IReadOnlyList<string> Conflicts { get; private set; } = [];
    public string ArchiveSummary { get; private set; } = "";
    public IReadOnlyList<ProfileModItem> VisibleEntries => Entries.Where(e => e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string Search { get => search; set { if (Set(ref search, value)) Notify(nameof(VisibleEntries)); } }
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
            Options = value is null ? null : new(value.Mod, value.Entry.Options);
            Notify(nameof(Options)); Notify(nameof(ToggleLabel)); Notify(nameof(SelectedModName)); Notify(nameof(HasSelectedMod)); RefreshCommands();
        }
    }
    public ModOptionsViewModel? Options { get; private set; }
    public Mod? ModToAdd { get => modToAdd; set { if (Set(ref modToAdd, value)) AddCommand.Refresh(); } }
    public string ToggleLabel => SelectedMod?.Entry.Enabled == true ? "Disable" : "Enable";
    public AsyncCommand MakeActiveCommand { get; }
    public AsyncCommand AddCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand ApplyOptionsCommand { get; }
    public AsyncCommand DeployCommand { get; }
    public AsyncCommand RunCommand { get; }
    public AsyncCommand PurgeCommand { get; }

    public ProfilesViewModel(AppServices services) : base(services)
    {
        MakeActiveCommand = Operations.CreateCommand("Selecting active profile", ct => Session.SaveProfileAsync(SelectedProfile!, true, ct), () => HasProfile);
        AddCommand = Operations.CreateCommand("Adding mod to profile", ct => Save(ProfileEditor.Add(SelectedProfile!, ModToAdd!), ct), () => HasProfile && ModToAdd is not null);
        RemoveCommand = Operations.CreateCommand("Removing mod from profile", ct => Save(ProfileEditor.Remove(SelectedProfile!, SelectedMod!.Mod.Id), ct), () => SelectedMod is not null);
        ToggleCommand = Operations.CreateCommand("Changing enabled mods", ct => Save(ProfileEditor.SetEnabled(SelectedProfile!, SelectedMod!.Mod.Id, !SelectedMod.Entry.Enabled), ct), () => SelectedMod is not null);
        ApplyOptionsCommand = Operations.CreateCommand("Saving mod options", ct => Save(ProfileEditor.SetOptions(SelectedProfile!, SelectedMod!.Mod, Options!.Selections()), ct), () => Options?.HasOptions == true);
        DeployCommand = Operations.CreateCommand("Deploying profile", async ct =>
        {
            var current = SelectedProfile!;
            if (await Services.Dialogs.ConfirmAsync("Deploy profile", $"Deploy {current.Name} to {Session.GameDirectory}? This replaces all mod patches in the game folder with the selected loadout.", "Deploy"))
                await Session.DeployAsync(current.Id, Services.Dialogs, ct);
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
        { if (e.PropertyName == nameof(OperationState.IsBusy)) foreach (var row in Entries) row.EnableCommand.Refresh(); };
        WatchSession();
    }
    private Task Save(Profile value, CancellationToken ct) => Session.SaveProfileAsync(value, false, ct);
    public Task MoveModAsync(Guid modId, Guid targetModId, bool after)
    {
        if (!Operations.CanInteract || SelectedProfile is null || modId == targetModId) return Task.CompletedTask;
        var entries = SelectedProfile.Entries;
        var from = entries.ToList().FindIndex(e => e.ModId == modId);
        var target = entries.ToList().FindIndex(e => e.ModId == targetModId);
        if (from < 0 || target < 0) return Task.CompletedTask;
        var destination = target + (after ? 1 : 0);
        if (from < destination) destination--;
        if (from == destination) return Task.CompletedTask;
        SelectedMod = Entries.Single(e => e.Mod.Id == modId);
        var moved = ProfileEditor.Move(SelectedProfile, modId, destination);
        return Operations.RunAsync("Reordering mods", ct => Save(moved, ct));
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
        var id = selectedMod?.Mod.Id;
        var mods = Session.State.Mods.ToDictionary(m => m.Id);
        var report = SelectedProfile is null ? new ConflictReport([], []) : ConflictAnalyzer.Analyze(ProfilePatches.Resolve(Session.State, SelectedProfile));
        var colliding = report.Resources.SelectMany(c => c.SourceIds).ToHashSet();
        Entries = SelectedProfile?.Entries.Select((e, index) => new ProfileModItem(mods[e.ModId], e, index,
            new AsyncCommand(() => Operations.RunAsync("Changing enabled mods", ct => Save(ProfileEditor.SetEnabled(SelectedProfile!, e.ModId, !e.Enabled), ct)),
                () => Operations.CanInteract, Operations.ReportError), colliding.Contains(e.ModId), Session.GetIconPath(mods[e.ModId]))).ToArray() ?? [];
        AvailableMods = Session.State.Mods.Where(m => Entries.All(e => e.Mod.Id != m.Id)).ToArray();
        Notify(nameof(Entries)); Notify(nameof(VisibleEntries)); Notify(nameof(EntrySummary)); Notify(nameof(AvailableMods));
        SelectedMod = Entries.FirstOrDefault(e => e.Mod.Id == id) ?? Entries.FirstOrDefault();
        ModToAdd = AvailableMods.FirstOrDefault();
        Conflicts = report.Resources.Select(c => $"{c.Archive} · {c.Resource.Id:x16}/{c.Resource.Type:x16} · {mods[c.WinningSourceId].Name} wins").ToArray();
        ArchiveSummary = $"{ModPresentation.Count(report.Archives.Count, "shared archive")} · {ModPresentation.Count(Conflicts.Count, "overlapping resource")}";
        Notify(nameof(Conflicts)); Notify(nameof(HasConflicts)); Notify(nameof(ArchiveSummary));
        Notify(nameof(HasProfile)); Notify(nameof(ActiveLabel));
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { MakeActiveCommand, AddCommand, RemoveCommand,
            ToggleCommand, ApplyOptionsCommand, DeployCommand, RunCommand, PurgeCommand }) command.Refresh();
    }
}
