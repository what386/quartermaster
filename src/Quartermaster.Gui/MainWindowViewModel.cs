using System.Collections.ObjectModel;
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
        [new(PageKind.Mods, "Library", "M3,6 L12,2 L21,6 L21,18 L12,22 L3,18 Z M3,6 L12,11 L21,6 M12,11 L12,22"),
         new(PageKind.Profiles, "Profiles", "M3,5 L21,5 M3,12 L21,12 M3,19 L21,19 M6,2 L6,8 M16,9 L16,15 M10,16 L10,22"),
         new(PageKind.Providers, "Providers", "M9,5 L6,5 C0,5 0,14 6,14 L10,14 M15,10 L18,10 C24,10 24,19 18,19 L14,19 M8,16 L16,8"),
         new(PageKind.Settings, "Settings", "M9,2 L15,2 L16,6 L20,8 L23,12 L20,16 L16,18 L15,22 L9,22 L8,18 L4,16 L1,12 L4,8 L8,6 Z M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8")];
    public NavigationItem LibraryNavigation => NavigationItems.Single(item => item.Page == PageKind.Mods);
    public IReadOnlyList<NavigationItem> UtilityNavigationItems => NavigationItems.Where(item => item.Page is PageKind.Providers or PageKind.Settings).ToArray();
    public OperationState Operations { get; }
    public NavigationItem SelectedNavigation
    {
        get => selected;
        set
        {
            if (value is null || !Set(ref selected, value)) return;
            foreach (var item in NavigationItems) item.IsActive = item == value;
            Notify(nameof(CurrentPage)); RefreshProfiles(); NotifyStatus();
        }
    }
    public ViewModelBase CurrentPage => pages[SelectedNavigation.Page];
    private Profile? DisplayProfile => services.Session.ActiveProfile;
    public string ProfileLabel => DisplayProfile?.Name ?? "No active profile";
    public string LibraryCount => services.Session.State.Mods.Count.ToString();
    public string SelectionSummary => $"{DisplayProfile?.Entries.Count(e => e.Enabled) ?? 0} selected for deployment";
    public string CollisionCount => DisplayProfile is { } profile
        ? ConflictAnalyzer.Analyze(ProfilePatches.Resolve(services.Session.State, profile)).Resources.Count.ToString() : "0";
    public ObservableCollection<SidebarProfile> SidebarProfiles { get; } = [];
    public AsyncCommand AddProfileCommand { get; }
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
        AddProfileCommand = Operations.CreateCommand("Creating profile", async ct =>
        {
            var name = await services.Dialogs.RequestTextAsync("Create profile", "Name your profile", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            ct.ThrowIfCancellationRequested();
            await services.Session.SaveProfileAsync(ProfileEditor.Create(name.Trim()), true, ct);
            Navigate(PageKind.Profiles);
        });
        services.Session.Changed += (_, _) =>
        {
            RefreshProfiles(); NotifyStatus();
            if (services.Session.State.Profiles.Count == 0 && SelectedNavigation.Page == PageKind.Profiles)
                Navigate(PageKind.Mods);
        };
        Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationState.IsBusy)) return;
            foreach (var entry in SidebarProfiles)
            { entry.SelectCommand.Refresh(); entry.RenameCommand.Refresh(); entry.DeleteCommand.Refresh(); }
        };
        RefreshProfiles();
        ((ProfilesViewModel)pages[PageKind.Profiles]).PropertyChanged += (_, e) =>
        { if (e.PropertyName is nameof(ProfilesViewModel.SelectedProfile) or nameof(ProfilesViewModel.Conflicts)) NotifyStatus(); };
    }
    private void RefreshProfiles()
    {
        var profiles = services.Session.State.Profiles;
        var ids = profiles.Select(profile => profile.Id).ToHashSet();
        for (var index = SidebarProfiles.Count - 1; index >= 0; index--)
            if (!ids.Contains(SidebarProfiles[index].Profile.Id)) SidebarProfiles.RemoveAt(index);
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var active = profile.Id == services.Session.State.ActiveProfileId && SelectedNavigation.Page == PageKind.Profiles;
            var deployed = services.Session.DeploymentProblem == "" && services.Session.Inspection is { NeedsPurge: false } inspection &&
                inspection.Ledger.SelectionId == profile.Id &&
                inspection.Ledger.Signature == DeploymentPlanner.Create(ProfilePatches.Resolve(services.Session.State, profile)).Signature;
            var existing = SidebarProfiles.FirstOrDefault(item => item.Profile.Id == profile.Id);
            if (existing is not null)
            {
                existing.Update(profile, active, deployed);
                var oldIndex = SidebarProfiles.IndexOf(existing);
                if (oldIndex != index) SidebarProfiles.Move(oldIndex, index);
                continue;
            }
            SidebarProfiles.Insert(index, new SidebarProfile(profile, active,
                new AsyncCommand(async () =>
                {
                    if (profile.Id != services.Session.State.ActiveProfileId)
                    {
                        await Operations.RunAsync("Selecting profile", ct => services.Session.SaveProfileAsync(
                            services.Session.State.Profiles.Single(p => p.Id == profile.Id), true, ct), showProgress: false);
                        if (Operations.IsError) return;
                    }
                    Navigate(PageKind.Profiles);
                }, () => Operations.CanInteract, Operations.ReportError),
                new AsyncCommand(() => RenameProfileAsync(profile.Id), () => Operations.CanInteract, Operations.ReportError),
                new AsyncCommand(() => DeleteProfileAsync(profile.Id), () => Operations.CanInteract, Operations.ReportError)) { IsDeployed = deployed });
        }
    }
    private bool deployingProfile;
    public async Task DeployProfileAsync(Guid id)
    {
        if (deployingProfile) return;
        deployingProfile = true;
        try
        {
            // A double-click can arrive while its first click is still saving the selection.
            if (Operations.IsBusy)
            {
                if (Operations.Message != "Selecting profile") return;
                await Operations.WhenIdle;
                if (Operations.IsError) return;
            }
            var profile = SidebarProfiles.FirstOrDefault(item => item.Profile.Id == id);
            if (profile is null) return;
            await profile.SelectCommand.ExecuteAsync();
            if (Operations.IsError || services.Session.State.ActiveProfileId != id) return;
            Navigate(PageKind.Profiles);
            await ((ProfilesViewModel)pages[PageKind.Profiles]).DeployCommand.ExecuteAsync();
        }
        finally { deployingProfile = false; }
    }
    private void NotifyStatus()
    {
        foreach (var name in new[] { nameof(ProfileLabel), nameof(LibraryCount), nameof(SelectionSummary), nameof(CollisionCount) }) Notify(name);
    }
    private Task RenameProfileAsync(Guid id) => Operations.RunAsync("Renaming profile", async ct =>
    {
        var profile = services.Session.State.Profiles.Single(p => p.Id == id);
        var name = await services.Dialogs.RequestTextAsync("Rename profile", "Profile name", "Rename", profile.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == profile.Name) return;
        await services.Session.SaveProfileAsync(profile with { Name = name.Trim() }, false, ct);
    });
    private Task DeleteProfileAsync(Guid id) => Operations.RunAsync("Deleting profile", async ct =>
    {
        var profile = services.Session.State.Profiles.Single(p => p.Id == id);
        if (await services.Dialogs.ConfirmAsync("Delete profile", $"Delete {profile.Name}? Your imported mods and deployed game files will remain.", "Delete"))
            await services.Session.DeleteProfileAsync(id, ct);
    });
    public void Navigate(PageKind page) => SelectedNavigation = NavigationItems.Single(n => n.Page == page);
    public Task InitializeAsync() => Operations.RunAsync("Loading library", services.Session.InitializeAsync);
}
