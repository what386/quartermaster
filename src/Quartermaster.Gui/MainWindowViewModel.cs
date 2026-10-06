using System.Collections.ObjectModel;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Search;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Settings;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly Dictionary<PageKind, ViewModelBase> pages;
    private NavigationItem selected;
    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
        [new(PageKind.Mods, "Library", "library.svg"),
         new(PageKind.Profiles, "Profiles", "sliders.svg"),
         new(PageKind.Search, "Search", "search.svg"),
         new(PageKind.Downloads, "Downloads", "downloads.svg"),
         new(PageKind.Settings, "Settings", "gear.svg")];
    public NavigationItem LibraryNavigation => NavigationItems.Single(item => item.Page == PageKind.Mods);
    public IReadOnlyList<NavigationItem> UtilityNavigationItems => NavigationItems.Where(item => item.Page is PageKind.Search or PageKind.Downloads or PageKind.Settings).ToArray();
    public OperationState Operations { get; }
    public ModDownloads Downloads => services.Downloads;
    public bool ShowDownloadProgress => Downloads.HasPendingDownloads &&
        (SelectedNavigation.Page != PageKind.Downloads || Downloads.SelectedTab != 0);
    public NavigationItem SelectedNavigation
    {
        get => selected;
        set
        {
            if (value is null || !Set(ref selected, value)) return;
            foreach (var item in NavigationItems) item.IsActive = item == value;
            Notify(nameof(CurrentPage)); Notify(nameof(ShowDownloadProgress)); RefreshProfiles(); NotifyStatus();
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
    public AsyncCommand ImportProfileCommand { get; }
    private readonly AppServices services;
    public MainWindowViewModel(AppServices services)
    {
        this.services = services; Operations = services.Operations;
        services.Downloads.Changed += (_, _) => Notify(nameof(ShowDownloadProgress));
        services.Downloads.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ModDownloads.SelectedTab)) Notify(nameof(ShowDownloadProgress));
        };
        services.Downloads.ManualChecksRequested += (_, _) => Navigate(PageKind.Downloads);
        selected = NavigationItems.Single(item => item.Page == PageKind.Profiles); selected.IsActive = true;
        foreach (var item in NavigationItems) item.OpenCommand = new(() => Navigate(item.Page));
        pages = new()
        {
            [PageKind.Mods] = new ModsViewModel(services),
            [PageKind.Profiles] = new ProfilesViewModel(services),
            [PageKind.Search] = new SearchViewModel(services),
            [PageKind.Downloads] = services.Downloads,
            [PageKind.Settings] = new SettingsViewModel(services)
        };
        AddProfileCommand = Operations.CreateCommand("Creating profile", async ct =>
        {
            var request = await services.Dialogs.RequestProfileCreationAsync();
            if (request is null) return;
            ct.ThrowIfCancellationRequested();
            if (request.FromFile)
            {
                var path = await services.Dialogs.PickProfileZipAsync();
                if (path is null) return;
                await services.Session.ImportProfileAsync(path, ct);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Name)) return;
                await services.Session.SaveProfileAsync(ProfileEditor.Create(request.Name.Trim()), true, ct);
            }
            Navigate(PageKind.Profiles);
        });
        ImportProfileCommand = Operations.CreateCommand("Importing profile", async ct =>
        {
            var path = await services.Dialogs.PickProfileZipAsync();
            if (path is null) return;
            await services.Session.ImportProfileAsync(path, ct);
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
            { entry.SelectCommand.Refresh(); entry.RenameCommand.Refresh(); entry.DeleteCommand.Refresh(); entry.ExportCommand.Refresh(); entry.DuplicateCommand.Refresh(); }
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
                new AsyncCommand(() => DeleteProfileAsync(profile.Id), () => Operations.CanInteract, Operations.ReportError),
                new AsyncCommand(() => ExportProfileAsync(profile.Id), () => Operations.CanInteract, Operations.ReportError),
                new AsyncCommand(() => DuplicateProfileAsync(profile.Id), () => Operations.CanInteract, Operations.ReportError))
            { IsDeployed = deployed });
        }
    }
    public async Task ImportDropsAsync(IReadOnlyList<string> paths, Guid? profileId, bool sidebar = false, bool acceptsMods = true)
    {
        if (!Operations.CanInteract || paths.Count == 0) return;
        Guid? importedProfileId = null;
        await Operations.RunAsync("Importing dropped files", async ct =>
            importedProfileId = await services.Session.ImportDropsAsync(paths, profileId, ct, sidebar, acceptsMods));
        if (Operations.IsError) return;
        if ((importedProfileId ?? profileId) is { } id && SidebarProfiles.FirstOrDefault(profile => profile.Profile.Id == id) is { } target)
            await target.SelectCommand.ExecuteAsync();
        else Navigate(PageKind.Mods);
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
    private Task DuplicateProfileAsync(Guid id) => Operations.RunAsync("Duplicating profile", async ct =>
    {
        var profile = services.Session.State.Profiles.Single(p => p.Id == id);
        await services.Session.SaveProfileAsync(ProfileEditor.Duplicate(profile), true, ct);
        Navigate(PageKind.Profiles);
    });
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
    private Task ExportProfileAsync(Guid id) => Operations.RunAsync("Exporting profile", async ct =>
    {
        var profile = services.Session.State.Profiles.Single(item => item.Id == id);
        var name = string.Concat(profile.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c));
        var path = await services.Dialogs.SaveProfileZipAsync(name + "-profile.zip");
        if (path is not null) await services.Session.ExportProfileAsync(id, path, ct);
    });
    public void Navigate(PageKind page) => SelectedNavigation = NavigationItems.Single(n => n.Page == page);
    public Task InitializeAsync() => Operations.RunAsync("Loading library", async ct =>
    {
        await services.Session.InitializeAsync(ct);
        await services.Providers.InitializeAsync(ct);
        await ((SettingsViewModel)pages[PageKind.Settings]).InitializeProviderSettingsAsync(ct);
    });
}
