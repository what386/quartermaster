using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public sealed record ProfileModItem(Mod Mod, ProfileEntry Entry, int Index, AsyncCommand EnableCommand, bool HasConflict) : IModRow
{
    public string Name => Mod.Name;
    public string Number => (Index + 1).ToString();
    public string Description => ModPresentation.Description(Mod);
    public string Monogram => ModPresentation.Monogram(Mod);
    public string Summary => $"{ModPresentation.Count(Mod.PatchSets.Count, "patch set")} · {Mod.Version ?? "Local import"}";
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
    private string newProfileName = "";
    private string search = "";
    private string profileName = "";
    private bool showSettings;
    private bool repair = true;
    private string preview = "Preview the deployment to check slot allocation.";
    public IReadOnlyList<Profile> Profiles { get; private set; } = [];
    public IReadOnlyList<ProfileModItem> Entries { get; private set; } = [];
    public IReadOnlyList<Mod> AvailableMods { get; private set; } = [];
    public IReadOnlyList<string> Conflicts { get; private set; } = [];
    public string ArchiveSummary { get; private set; } = "";
    public IReadOnlyList<ProfileModItem> VisibleEntries => Entries.Where(e => e.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string Search { get => search; set { if (Set(ref search, value)) Notify(nameof(VisibleEntries)); } }
    public string EntrySummary => $"{Entries.Count} mods · {Entries.Count(e => e.Entry.Enabled)} on";
    public string SelectedModName => SelectedMod?.Name ?? "Select a mod";
    public bool HasProfile => SelectedProfile is not null;
    public bool HasConflicts => Conflicts.Count > 0;
    public string PriorityLabel => SelectedProfile?.Priority == PriorityDirection.FirstWins ? "Priority: earlier entries win" : "Priority: later entries win";
    public string ActiveLabel => SelectedProfile?.Id == Session.State.ActiveProfileId ? "Active profile" : "";
    public Profile? SelectedProfile
    {
        get => profile;
        set { if (Set(ref profile, value)) { ProfileName = value?.Name ?? ""; RebuildEntries(); } }
    }
    public ProfileModItem? SelectedMod
    {
        get => selectedMod;
        set
        {
            if (!Set(ref selectedMod, value)) return;
            Options = value is null ? null : new(value.Mod, value.Entry.Options);
            Notify(nameof(Options)); Notify(nameof(ToggleLabel)); Notify(nameof(SelectedModName)); RefreshCommands();
        }
    }
    public ModOptionsViewModel? Options { get; private set; }
    public Mod? ModToAdd { get => modToAdd; set { if (Set(ref modToAdd, value)) AddCommand.Refresh(); } }
    public string NewProfileName { get => newProfileName; set { if (Set(ref newProfileName, value)) CreateCommand.Refresh(); } }
    public string ProfileName { get => profileName; set { if (Set(ref profileName, value)) RenameCommand.Refresh(); } }
    public bool ShowSettings { get => showSettings; set => Set(ref showSettings, value); }
    public bool Repair { get => repair; set => Set(ref repair, value); }
    public string PreviewSummary { get => preview; private set => Set(ref preview, value); }
    public string DeploymentHealth => Session.DeploymentStatus;
    public bool NeedsPurge => Session.Inspection?.NeedsPurge == true || Session.DeploymentProblem != "";
    public string ToggleLabel => SelectedMod?.Entry.Enabled == true ? "Disable" : "Enable";
    public AsyncCommand CreateCommand { get; }
    public AsyncCommand RenameCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand MakeActiveCommand { get; }
    public AsyncCommand PriorityCommand { get; }
    public AsyncCommand AddCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand MoveUpCommand { get; }
    public AsyncCommand MoveDownCommand { get; }
    public AsyncCommand ApplyOptionsCommand { get; }
    public AsyncCommand PreviewCommand { get; }
    public AsyncCommand DeployCommand { get; }
    public AsyncCommand RunCommand { get; }
    public AsyncCommand PurgeCommand { get; }

    public ProfilesViewModel(AppServices services) : base(services)
    {
        CreateCommand = Operations.CreateCommand("Creating profile", async ct =>
        {
            var created = ProfileEditor.Create(NewProfileName);
            await Session.SaveProfileAsync(created, true, ct);
            SelectedProfile = Profiles.Single(p => p.Id == created.Id); NewProfileName = "";
        }, () => !string.IsNullOrWhiteSpace(NewProfileName));
        RenameCommand = Operations.CreateCommand("Renaming profile", ct => Save(SelectedProfile! with { Name = ProfileName.Trim() }, ct),
            () => HasProfile && !string.IsNullOrWhiteSpace(ProfileName) && ProfileName.Trim() != SelectedProfile!.Name);
        DeleteCommand = Operations.CreateCommand("Deleting profile", async ct =>
        {
            var current = SelectedProfile!;
            if (await Services.Dialogs.ConfirmAsync("Delete profile", $"Delete {current.Name}? Your imported mods and deployed game files will remain.", "Delete"))
                await Session.DeleteProfileAsync(current.Id, ct);
        }, () => HasProfile);
        MakeActiveCommand = Operations.CreateCommand("Selecting active profile", ct => Session.SaveProfileAsync(SelectedProfile!, true, ct), () => HasProfile);
        PriorityCommand = Operations.CreateCommand("Changing priority", ct => Save(SelectedProfile! with
        { Priority = SelectedProfile!.Priority == PriorityDirection.LastWins ? PriorityDirection.FirstWins : PriorityDirection.LastWins }, ct), () => HasProfile);
        AddCommand = Operations.CreateCommand("Adding mod to profile", ct => Save(ProfileEditor.Add(SelectedProfile!, ModToAdd!), ct), () => HasProfile && ModToAdd is not null);
        RemoveCommand = Operations.CreateCommand("Removing mod from profile", ct => Save(ProfileEditor.Remove(SelectedProfile!, SelectedMod!.Mod.Id), ct), () => SelectedMod is not null);
        ToggleCommand = Operations.CreateCommand("Changing enabled mods", ct => Save(ProfileEditor.SetEnabled(SelectedProfile!, SelectedMod!.Mod.Id, !SelectedMod.Entry.Enabled), ct), () => SelectedMod is not null);
        MoveUpCommand = Operations.CreateCommand("Moving mod", ct => Save(ProfileEditor.Move(SelectedProfile!, SelectedMod!.Mod.Id, SelectedMod.Index - 1), ct), () => SelectedMod?.Index > 0);
        MoveDownCommand = Operations.CreateCommand("Moving mod", ct => Save(ProfileEditor.Move(SelectedProfile!, SelectedMod!.Mod.Id, SelectedMod.Index + 1), ct), () => SelectedMod is not null && SelectedMod.Index < Entries.Count - 1);
        ApplyOptionsCommand = Operations.CreateCommand("Saving mod options", ct => Save(ProfileEditor.SetOptions(SelectedProfile!, SelectedMod!.Mod, Options!.Selections()), ct), () => Options?.HasOptions == true);
        PreviewCommand = Operations.CreateCommand("Checking deployment", async ct =>
        {
            var plan = await Session.PreviewAsync(SelectedProfile!.Id, ct);
            PreviewSummary = $"{plan.Patches.Count} patch sets · {plan.Patches.Sum(p => p.Files.Count)} files · {Conflicts.Count} resource collisions";
        }, () => HasProfile && Session.GameDirectory != "");
        DeployCommand = Operations.CreateCommand("Deploying profile", async ct =>
        {
            var current = SelectedProfile!; var plan = await Session.PreviewAsync(current.Id, ct);
            if (await Services.Dialogs.ConfirmAsync("Deploy profile", $"Deploy {current.Name} to {Session.GameDirectory}? This replaces all mod patches in the game folder with {plan.Patches.Count} patch sets ({plan.Patches.Sum(p => p.Files.Count)} files).", "Deploy"))
                await Session.DeployAsync(current.Id, Repair, ct);
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
            if (await Services.Dialogs.ConfirmAsync("Purge patches", "Remove all mod patch files from the selected game folder? Imported originals, repaired copies and profiles will remain.", "Purge"))
                await Session.PurgeAsync(ct);
        }, () => Session.GameDirectory != "");
        Operations.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(OperationState.IsBusy)) foreach (var row in Entries) row.EnableCommand.Refresh(); };
        WatchSession();
    }
    private Task Save(Profile value, CancellationToken ct) => Session.SaveProfileAsync(value, false, ct);
    protected override void Refresh()
    {
        Profiles = Session.State.Profiles.ToArray(); Notify(nameof(Profiles));
        Notify(nameof(DeploymentHealth)); Notify(nameof(NeedsPurge));
        SelectedProfile = Session.ActiveProfile ?? Profiles.FirstOrDefault();
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
                () => Operations.CanInteract, Operations.ReportError), colliding.Contains(e.ModId))).ToArray() ?? [];
        AvailableMods = Session.State.Mods.Where(m => Entries.All(e => e.Mod.Id != m.Id)).ToArray();
        Notify(nameof(Entries)); Notify(nameof(VisibleEntries)); Notify(nameof(EntrySummary)); Notify(nameof(AvailableMods));
        SelectedMod = Entries.FirstOrDefault(e => e.Mod.Id == id) ?? Entries.FirstOrDefault();
        ModToAdd = AvailableMods.FirstOrDefault();
        Conflicts = report.Resources.Select(c => $"{c.Archive} · {c.Resource.Id:x16}/{c.Resource.Type:x16} · {mods[c.WinningSourceId].Name} wins").ToArray();
        ArchiveSummary = $"{ModPresentation.Count(report.Archives.Count, "shared archive")} · {ModPresentation.Count(Conflicts.Count, "overlapping resource")}";
        Notify(nameof(Conflicts)); Notify(nameof(HasConflicts)); Notify(nameof(ArchiveSummary));
        Notify(nameof(HasProfile)); Notify(nameof(PriorityLabel)); Notify(nameof(ActiveLabel));
        PreviewSummary = "Preview the deployment to check slot allocation.";
        RefreshCommands();
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { RenameCommand, DeleteCommand, MakeActiveCommand, PriorityCommand, AddCommand, RemoveCommand,
            ToggleCommand, MoveUpCommand, MoveDownCommand, ApplyOptionsCommand, PreviewCommand, DeployCommand, RunCommand, PurgeCommand }) command.Refresh();
    }
}
