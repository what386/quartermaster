using Quartermaster.Core.Deployment;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Providers;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly Dictionary<PageKind, ViewModelBase> pages;
    private NavigationItem selected;
    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
        [new(PageKind.Mods, "Mods", "M3,6 L12,2 L21,6 L21,18 L12,22 L3,18 Z M3,6 L12,11 L21,6 M12,11 L12,22"),
         new(PageKind.Profiles, "Profiles", "M3,5 L21,5 M3,12 L21,12 M3,19 L21,19 M6,2 L6,8 M16,9 L16,15 M10,16 L10,22"),
         new(PageKind.Providers, "Providers", "M9,5 L6,5 C0,5 0,14 6,14 L10,14 M15,10 L18,10 C24,10 24,19 18,19 L14,19 M8,16 L16,8"),
         new(PageKind.Settings, "Settings", "M9,2 L15,2 L16,6 L20,8 L23,12 L20,16 L16,18 L15,22 L9,22 L8,18 L4,16 L1,12 L4,8 L8,6 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8")];
    public OperationState Operations { get; }
    public NavigationItem SelectedNavigation
    {
        get => selected;
        set
        {
            if (value is null || !Set(ref selected, value)) return;
            foreach (var item in NavigationItems) item.IsActive = item == value;
            Notify(nameof(CurrentPage)); Notify(nameof(PageTitle)); NotifyStatus();
        }
    }
    public ViewModelBase CurrentPage => pages[SelectedNavigation.Page];
    public string PageTitle => SelectedNavigation.Label.ToUpperInvariant();
    private Profile? DisplayProfile => services.Session.ActiveProfile;
    public string ProfileLabel => DisplayProfile?.Name ?? "No active profile";
    public string LibraryCount => services.Session.State.Mods.Count.ToString();
    public string ProfileCount => services.Session.State.Profiles.Count.ToString();
    public string SelectionSummary => $"{DisplayProfile?.Entries.Count(e => e.Enabled) ?? 0} selected for deployment";
    public string DeployedCount => (services.Session.Inspection?.Ledger.Files.Select(f => f.SourceId).Distinct().Count() ?? 0).ToString();
    public string CollisionCount => DisplayProfile is { } profile
        ? ConflictAnalyzer.Analyze(ProfilePatches.Resolve(services.Session.State, profile)).Resources.Count.ToString() : "0";
    public string GameFolder => services.Session.GameDirectory == "" ? "Choose a game folder in Settings" : services.Session.GameDirectory;
    public string GameState => services.Session.GameDirectory == "" ? "NOT SET" : services.Session.DeploymentProblem != "" || services.Session.Inspection?.NeedsPurge == true ? "CHECK" : "FOUND";
    public string DeploymentSummary => services.Session.DeploymentStatus;
    public IReadOnlyList<SidebarProfile> SidebarProfiles { get; private set; } = [];
    public Command AddProfileCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    private readonly AppServices services;
    public MainWindowViewModel(AppServices services)
    {
        this.services = services; Operations = services.Operations;
        selected = NavigationItems.Single(item => item.Page == PageKind.Profiles); selected.IsActive = true;
        foreach (var item in NavigationItems) item.OpenCommand = new(() => Navigate(item.Page));
        pages = new()
        {
            [PageKind.Mods] = new ModsViewModel(services),
            [PageKind.Profiles] = new ProfilesViewModel(services),
            [PageKind.Providers] = new ProvidersViewModel(),
            [PageKind.Settings] = new SettingsViewModel(services)
        };
        RefreshCommand = Operations.CreateCommand("Refreshing library", services.Session.ReloadAsync);
        AddProfileCommand = new(() =>
        {
            Navigate(PageKind.Profiles);
            ((ProfilesViewModel)pages[PageKind.Profiles]).ShowSettings = true;
        }, () => Operations.CanInteract);
        services.Session.Changed += (_, _) => { RefreshProfiles(); NotifyStatus(); };
        Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationState.IsBusy)) return;
            AddProfileCommand.Refresh();
            foreach (var entry in SidebarProfiles) entry.SelectCommand.Refresh();
        };
        RefreshProfiles();
        ((ProfilesViewModel)pages[PageKind.Profiles]).PropertyChanged += (_, e) =>
        { if (e.PropertyName is nameof(ProfilesViewModel.SelectedProfile) or nameof(ProfilesViewModel.Conflicts)) NotifyStatus(); };
    }
    private void RefreshProfiles()
    {
        SidebarProfiles = services.Session.State.Profiles.Select(profile => new SidebarProfile(profile,
            profile.Id == services.Session.State.ActiveProfileId,
            new AsyncCommand(() => profile.Id == services.Session.State.ActiveProfileId ? Task.CompletedTask :
                Operations.RunAsync("Selecting profile", ct => services.Session.SaveProfileAsync(
                    services.Session.State.Profiles.Single(p => p.Id == profile.Id), true, ct)),
                () => Operations.CanInteract, Operations.ReportError))).ToArray();
        Notify(nameof(SidebarProfiles));
    }
    private void NotifyStatus()
    {
        foreach (var name in new[] { nameof(ProfileLabel), nameof(LibraryCount), nameof(ProfileCount), nameof(SelectionSummary),
            nameof(DeployedCount), nameof(CollisionCount), nameof(GameFolder), nameof(GameState), nameof(DeploymentSummary) }) Notify(name);
    }
    public void Navigate(PageKind page) => SelectedNavigation = NavigationItems.Single(n => n.Page == page);
    public Task InitializeAsync() => Operations.RunAsync("Loading library", services.Session.InitializeAsync);
}
