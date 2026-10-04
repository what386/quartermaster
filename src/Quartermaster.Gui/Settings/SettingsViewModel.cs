using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Settings;

public sealed class SettingsViewModel : SessionViewModel
{
    private string gamePath = "";
    private string savedPath = "";
    private string? selectedInstallation;
    private int repatchChoice;
    public IReadOnlyList<string> RepatchChoices { get; } = ["Ask when needed", "Automatically repatch when needed", "Never repatch"];
    public int RepatchChoice { get => repatchChoice; set { if (Set(ref repatchChoice, value)) SaveRepatchCommand.Refresh(); } }
    public AsyncCommand SaveRepatchCommand { get; }
    public string GamePath { get => gamePath; set { if (Set(ref gamePath, value)) SavePathCommand.Refresh(); } }
    public string? SelectedInstallation { get => selectedInstallation; set { if (Set(ref selectedInstallation, value)) UseInstallationCommand.Refresh(); } }
    public IReadOnlyList<string> Installations { get; private set; } = [];
    public string LibraryDirectory => Services.DataDirectory;
    public string DeploymentStatus => Session.DeploymentStatus;
    public string ActiveProfileName => Session.ActiveProfile?.Name ?? "No profile selected";
    public string PriorityLabel => Session.ActiveProfile?.Priority == PriorityDirection.FirstWins ? "Earlier entries win" : "Later entries win";
    public AsyncCommand PriorityCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand SavePathCommand { get; }
    public AsyncCommand DiscoverCommand { get; }
    public AsyncCommand UseInstallationCommand { get; }
    public AsyncCommand PurgeCommand { get; }
    public SettingsViewModel(AppServices services) : base(services)
    {
        SaveRepatchCommand = Operations.CreateCommand("Saving repatch setting", ct => Session.SetRepatchModeAsync((RepatchMode)RepatchChoice, ct),
            () => RepatchChoice >= 0 && RepatchChoice < RepatchChoices.Count && (RepatchMode)RepatchChoice != Session.Settings.Repatch);
        PriorityCommand = Operations.CreateCommand("Changing load priority", ct =>
        {
            var profile = Session.ActiveProfile!;
            return Session.SaveProfileAsync(profile with
            { Priority = profile.Priority == PriorityDirection.LastWins ? PriorityDirection.FirstWins : PriorityDirection.LastWins }, false, ct);
        }, () => Session.ActiveProfile is not null);
        BrowseCommand = Operations.CreateCommand("Selecting game folder", async ct =>
        {
            var path = await Services.Dialogs.PickFolderAsync("Choose Helldivers 2 installation or data folder");
            if (path is not null) await Session.SetGameDirectoryAsync(path, ct);
        });
        SavePathCommand = Operations.CreateCommand("Saving game folder", ct => Session.SetGameDirectoryAsync(GamePath, ct), () => !string.IsNullOrWhiteSpace(GamePath));
        DiscoverCommand = Operations.CreateCommand("Finding Steam installations", async ct =>
        {
            Installations = await Session.DiscoverAsync(ct); Notify(nameof(Installations));
            SelectedInstallation = Installations.FirstOrDefault();
        });
        UseInstallationCommand = Operations.CreateCommand("Selecting game folder", ct => Session.SetGameDirectoryAsync(SelectedInstallation!, ct), () => SelectedInstallation is not null);
        PurgeCommand = Operations.CreateCommand("Purging patches", async ct =>
        {
            if (await Services.Dialogs.ConfirmAsync("Purge patches", "Remove all mod patch files from the selected game folder? Your library originals will remain. Deploy your profile again afterwards.", "Purge"))
                await Session.PurgeAsync(ct);
        }, () => Session.GameDirectory != "");
        WatchSession();
    }
    protected override void Refresh()
    {
        if (savedPath != Session.GameDirectory) { savedPath = Session.GameDirectory; GamePath = savedPath; }
        RepatchChoice = (int)Session.Settings.Repatch; SaveRepatchCommand.Refresh();
        Notify(nameof(DeploymentStatus)); Notify(nameof(ActiveProfileName)); Notify(nameof(PriorityLabel));
        PurgeCommand.Refresh(); PriorityCommand.Refresh();
    }
}
