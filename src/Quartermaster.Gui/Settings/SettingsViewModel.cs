using System.Reflection;
using System.Runtime.InteropServices;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Settings;

public sealed partial class SettingsViewModel : SessionViewModel
{
    private string gamePath = "";
    private string? selectedInstallation;
    private string search = "";
    private int repatchChoice;
    private int priorityChoice;
    private ApplicationSettings? saved;
    private Guid? profileId;
    private int savedPriority;
    public IReadOnlyList<string> RepatchChoices { get; } = ["Ask when needed", "Automatically repatch when needed", "Never repatch"];
    public IReadOnlyList<string> PriorityChoices { get; } = ["Later entries win", "Earlier entries win"];
    public int RepatchChoice { get => repatchChoice; set { if (Set(ref repatchChoice, value)) SaveCommand.Refresh(); } }
    public int PriorityChoice { get => priorityChoice; set { if (Set(ref priorityChoice, value)) SaveCommand.Refresh(); } }
    public string GamePath { get => gamePath; set { if (Set(ref gamePath, value)) SaveCommand.Refresh(); } }
    public string? SelectedInstallation
    {
        get => selectedInstallation;
        set { if (Set(ref selectedInstallation, value) && value is not null) GamePath = value; }
    }
    public string Search
    {
        get => search;
        set
        {
            if (!Set(ref search, value)) return;
            foreach (var name in new[] { nameof(ShowInstallation), nameof(ShowPriority), nameof(ShowRepatch), nameof(ShowStorage),
                nameof(ShowVersion), nameof(ShowPlatform), nameof(ShowRuntime), nameof(ShowLogs), nameof(ShowConfiguration), nameof(ShowNexus), nameof(ShowGitHub), nameof(ShowAppearance),
                nameof(ShowDownloads), nameof(ShowAppSettings), nameof(ShowGameSettings), nameof(ShowProviderSettings), nameof(ShowAppInformation), nameof(HasMatches) }) Notify(name);
        }
    }
    private bool Matches(string keywords) => string.IsNullOrWhiteSpace(Search) || keywords.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase);
    public bool ShowInstallation => Matches("Game and deployment Helldivers 2 installation folder Steam path");
    public bool ShowPriority => Matches("Game and deployment Profile load priority earlier later entries order " + ActiveProfileName);
    public bool ShowRepatch => Matches("Game and deployment Repatching repair automatic ask never patches");
    public bool ShowStorage => Matches("App information Library storage directory originals profiles " + LibraryDirectory);
    public bool ShowVersion => Matches("App information Quartermaster application version build " + ApplicationVersion);
    public bool ShowPlatform => Matches("App information Operating system platform architecture " + PlatformInformation);
    public bool ShowRuntime => Matches("App information Runtime .NET " + RuntimeVersion);
    public bool ShowLogs => Matches("App information Log file diagnostics troubleshooting " + LogFilePath);
    public bool ShowConfiguration => Matches("App information Settings configuration file " + ConfigurationFilePath);
    public bool ShowDownloads => Matches("Downloads download scanning browser ZIP watch folder directory manual updates");
    public bool ShowAppSettings => ShowAppearance;
    public bool ShowGameSettings => ShowInstallation || ShowPriority || ShowRepatch;
    public bool ShowProviderSettings => ShowNexus || ShowGitHub;
    public bool ShowAppInformation => ShowStorage || ShowVersion || ShowPlatform || ShowRuntime || ShowLogs || ShowConfiguration;
    public bool HasMatches => ShowAppSettings || ShowGameSettings || ShowDownloads || ShowProviderSettings || ShowAppInformation;
    public IReadOnlyList<string> Installations { get; private set; } = [];
    public bool HasInstallations => Installations.Count > 0;
    public string LibraryDirectory => Services.DataDirectory;
    public string ApplicationVersion { get; } = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString() ?? "Unknown";
    public string PlatformInformation { get; } = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
    public string RuntimeVersion { get; } = RuntimeInformation.FrameworkDescription;
    public string LogFilePath => Path.Combine(LibraryDirectory, "log.jsonl");
    public string ConfigurationFilePath => Path.Combine(LibraryDirectory, "settings.json");
    public string ActiveProfileName => Session.ActiveProfile?.Name ?? "No profile selected";
    public bool HasProfile => Session.ActiveProfile is not null;
    public AsyncCommand SaveCommand { get; }
    public Command ResetCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public AsyncCommand DiscoverCommand { get; }
    public SettingsViewModel(AppServices services) : base(services)
    {
        SaveCommand = Operations.CreateCommand("Saving settings", async ct =>
        {
            var account = await ValidateProviderDraftAsync(ct);
            await Session.SaveSettingsAsync(GamePath.Trim(), (RepatchMode)RepatchChoice, profileId, (PriorityDirection)PriorityChoice, ct,
                (ThemePreset)ThemeChoice, ThemeManager.Format(AccentColor));
            await SaveProviderDraftAsync(account, ct);
            LoadDrafts();
        }, () => ValidAppearance && RepatchChoice >= 0 && RepatchChoice < RepatchChoices.Count && PriorityChoice >= 0 && PriorityChoice < PriorityChoices.Count &&
            (GamePath.Trim() != Session.GameDirectory || RepatchChoice != (int)Session.Settings.Repatch ||
             HasProfile && PriorityChoice != (int)Session.ActiveProfile!.Priority || ProviderDraftChanged || AppearanceChanged));
        ResetCommand = new(() =>
        {
            var defaults = new ApplicationSettings();
            SelectedInstallation = null;
            GamePath = defaults.GameDataDirectory ?? "";
            RepatchChoice = (int)defaults.Repatch;
            PriorityChoice = (int)PriorityDirection.LastWins;
            ThemeChoice = (int)defaults.Theme;
            AccentColor = Avalonia.Media.Color.Parse(defaults.AccentColor);
            ResetProviderDraft();
        }, () => Operations.CanInteract);
        BrowseCommand = Operations.CreateCommand("Selecting game folder", async _ =>
        {
            var path = await Services.Dialogs.PickFolderAsync("Choose Helldivers 2 installation or data folder");
            if (path is not null) GamePath = path;
        });
        DiscoverCommand = Operations.CreateCommand("Finding Steam installations", async ct =>
        {
            SelectedInstallation = null;
            Installations = await Session.DiscoverAsync(ct); Notify(nameof(Installations)); Notify(nameof(HasInstallations));
            SelectedInstallation = Installations.FirstOrDefault();
        });
        InitializeProviderCommands();
        WatchSession();
    }
    private void LoadDrafts()
    {
        saved = Session.Settings; profileId = Session.ActiveProfile?.Id;
        savedPriority = (int)(Session.ActiveProfile?.Priority ?? PriorityDirection.LastWins);
        GamePath = Session.GameDirectory; RepatchChoice = (int)Session.Settings.Repatch; PriorityChoice = savedPriority;
        LoadAppearance();
        SaveCommand.Refresh();
    }
    protected override void Refresh()
    {
        if (saved is null) LoadDrafts();
        else
        {
            // Keep unsaved edits when unrelated session state refreshes.
            if (GamePath == (saved.GameDataDirectory ?? "")) GamePath = Session.GameDirectory;
            if (RepatchChoice == (int)saved.Repatch) RepatchChoice = (int)Session.Settings.Repatch;
            var currentPriority = (int)(Session.ActiveProfile?.Priority ?? PriorityDirection.LastWins);
            if (profileId != Session.ActiveProfile?.Id || PriorityChoice == savedPriority) PriorityChoice = currentPriority;
            RefreshAppearance(saved);
            saved = Session.Settings; profileId = Session.ActiveProfile?.Id; savedPriority = currentPriority;
        }
        Notify(nameof(ActiveProfileName)); Notify(nameof(HasProfile)); Notify(nameof(ShowPriority)); Notify(nameof(ShowGameSettings)); Notify(nameof(HasMatches)); SaveCommand.Refresh();
    }
}
