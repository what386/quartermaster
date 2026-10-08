using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class ModDetailsTests
{
    [AvaloniaFact]
    public async Task DependencyStatesAndActionsFollowTheLibraryAndSelectedProfile()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var root = await f.Services.Library.ImportAsync(f.Source("Root", 9));
        var installed = new List<Mod>();
        for (var id = 1; id <= 3; id++)
        {
            var mod = await f.Services.Library.ImportAsync(f.Source("Dep" + id, (ulong)id));
            await f.Services.Library.SetSourcesAsync(mod.Id, [new("nexusmods", id.ToString(), (id * 10).ToString())]);
            installed.Add(mod);
        }
        await f.Services.Library.SetSourcesAsync(root.Id, [new("nexusmods", "9", "90")]);
        await f.Services.Library.SetDependenciesAsync(root.Id, Enumerable.Range(1, 4).Select(id =>
            new ModDependency("Dep" + id, $"https://www.nexusmods.com/helldivers2/mods/{id}")).ToArray());
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, root), installed[0]), installed[1]);
        profile = ProfileEditor.SetEnabled(profile, installed[1].Id, false);
        await f.Services.Session.SaveProfileAsync(profile, true, CancellationToken.None);
        var library = new ModDetailsViewModel(root, f.Services);
        Assert.Equal(new[] { "Installed", "Installed", "Installed", "Missing" }, library.Dependencies.Select(item => item.Status));
        Assert.Equal("Install missing dependencies (1)", library.DependencyActionLabel);
        var details = new ModDetailsViewModel(root, f.Services, profile.Id);
        Assert.Equal(new[] { "Installed", "Disabled", "Not in this profile", "Missing" }, details.Dependencies.Select(item => item.Status));
        Assert.Equal("Add dependencies to profile (3)", details.DependencyActionLabel);
        Assert.True(details.HasDependencyAction);
        await f.Services.Library.SetDependenciesAsync(root.Id, [new("Dep1", "https://www.nexusmods.com/helldivers2/mods/1")]);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var satisfied = new ModDetailsViewModel(root, f.Services, profile.Id);
        Assert.False(satisfied.HasDependencyAction);
        Assert.Equal("All dependencies available.", satisfied.DependencySummary);
        Assert.False(satisfied.GetDependenciesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CollisionsBelongToTheSelectedModAndRespectTheProfileContext()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var selected = await f.Services.Library.ImportAsync(f.Source("Selected", 1));
        var winner = await f.Services.Library.ImportAsync(f.Source("Winner", 1));
        var unrelated = await f.Services.Library.ImportAsync(f.Source("Unrelated", 2));
        var unrelatedWinner = await f.Services.Library.ImportAsync(f.Source("Unrelated winner", 2));
        var clear = await f.Services.Library.ImportAsync(f.Source("Clear", 3));
        var primary = f.Services.Session.ActiveProfile! with { Name = "Primary" };
        foreach (var mod in new[] { selected, winner, unrelated, unrelatedWinner, clear })
            primary = ProfileEditor.Add(primary, mod);
        await f.Services.Session.SaveProfileAsync(primary, true, CancellationToken.None);
        var secondary = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Secondary"), selected), winner);
        await f.Services.Session.SaveProfileAsync(secondary, false, CancellationToken.None);
        var profiles = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        profiles.SelectedMod = profiles.Entries.Single(row => row.Mod.Id == selected.Id);
        var details = profiles.Details!;
        Assert.True(details.HasCollisions);
        var collision = Assert.Single(details.Collisions);
        Assert.Contains("Primary", collision); Assert.Contains("Clashes with Winner", collision);
        Assert.Contains("Winner wins", collision); Assert.DoesNotContain("Unrelated", collision);
        Assert.Equal("1 overlapping resource with Winner.", details.CollisionSummary);
        Assert.True(profiles.SelectedMod.HasWarnings);
        Assert.False(new ModDetailsViewModel(clear, f.Services).HasCollisions);
        var libraryDetails = new ModDetailsViewModel(selected, f.Services);
        Assert.Equal(2, libraryDetails.Collisions.Count);
        Assert.Equal("1 overlapping resource with Winner across 2 profiles.", libraryDetails.CollisionSummary);
        Assert.Contains(libraryDetails.Collisions, item => item.StartsWith("Secondary"));
        var dialog = new ModDetailsDialog { DataContext = details };
        dialog.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 2;
        var window = new Window { Content = dialog }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(1, dialog.FindControl<ItemsControl>("CollisionsList")!.ItemCount);
            primary = ProfileEditor.Move(primary, selected.Id, 1);
            await f.Services.Session.SaveProfileAsync(primary, true, CancellationToken.None);
            Assert.Contains("Selected wins", Assert.Single(details.Collisions));
            await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(primary, winner.Id, false), true, CancellationToken.None);
            Assert.False(details.HasCollisions);
            Assert.Empty(details.CollisionSummary);
            Assert.False(profiles.Entries.Single(row => row.Mod.Id == selected.Id).HasWarnings);
            Assert.Single(new ModDetailsViewModel(selected, f.Services).Collisions);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClosingDetailsSavesLinkAndInvalidLinkKeepsTheDialogOpen()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var mod = await f.Services.Library.ImportAsync(f.Source("Example"));
        var details = new ModDetailsViewModel(mod, f.Services);
        var dialog = new ModDetailsDialog { DataContext = details };
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        var shown = window.ShowDialogAsync<object?>(dialog);
        try
        {
            details.PageLink = "invalid link";
            dialog.Cancel(); await f.Services.Operations.WhenIdle;
            Assert.False(shown.IsCompleted); Assert.True(f.Services.Operations.IsError);
            details.PageLink = "https://example.com/mod";
            dialog.Cancel(); await shown;
            Assert.Equal(details.PageLink, Assert.Single(f.Services.Session.State.Mods).PageLink);
            Assert.Empty(f.BrowserRequests);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task RetainedVersionsAreLabelledAndProfilesShowTheUpdatedProviderVersion()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        var old = await f.Services.Library.ImportAsync(f.Source("Old", 1), "Example");
        var updated = await f.Services.Library.ImportAsync(f.Source("New", 2), "Example");
        Assert.Null(old.Version); Assert.Null(updated.Version);
        await f.Services.Library.SetSourcesAsync(old.Id, [new("nexusmods", "100", "10", "1.0")]);
        await f.Services.Library.SetSourcesAsync(updated.Id, [new("nexusmods", "100", "11", "2.0")]);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.Add(f.Services.Session.ActiveProfile!, old), false, CancellationToken.None);
        await f.Services.Library.ReplaceInProfilesAsync(old.Id, updated.Id);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var profile = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        var row = Assert.Single(profile.Entries);
        Assert.Equal(updated.Id, row.Mod.Id); Assert.Equal("Example · 2.0", row.Title);
        Assert.Equal("2.0", new ModDetailsViewModel(row.Mod, f.Services).Version);
        f.Shell.Navigate(PageKind.Mods);
        var library = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
        Assert.Equal(2, library.Mods.Count);
        Assert.Equal("Example · 1.0 (previous version)", library.Mods.Single(item => item.Mod.Id == old.Id).Title);
        Assert.Equal("Example · 2.0", library.Mods.Single(item => item.Mod.Id == updated.Id).Title);
    }

    [AvaloniaFact]
    public async Task DetailsShowSavedRequirementsAndReverseDependenciesFromTheLibrary()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Requirement", 1), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Dependent", 2), CancellationToken.None);
        var requirement = f.Services.Session.State.Mods.Single(mod => mod.Name == "Requirement");
        var dependent = f.Services.Session.State.Mods.Single(mod => mod.Name == "Dependent");
        await f.Services.Library.SetSourcesAsync(requirement.Id, [new("nexusmods", "100", "1")]);
        await f.Services.Library.SetDependenciesAsync(dependent.Id, [
            new("Requirement", "https://nexusmods.com/helldivers2/mods/100", "Install this first."),
            new("Missing", "https://www.nexusmods.com/helldivers2/mods/200"),
            new("External", "https://example.com/setup", "Follow the setup instructions.", false)]);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        requirement = f.Services.Session.State.Mods.Single(mod => mod.Id == requirement.Id);
        dependent = f.Services.Session.State.Mods.Single(mod => mod.Id == dependent.Id);
        var details = new ModDetailsViewModel(dependent, f.Services);
        Assert.True(details.HasDependencies); Assert.False(details.HasDependents);
        Assert.Equal(["Installed", "Missing", "Manual installation"], details.Dependencies.Select(item => item.Status));
        Assert.Equal("Install this first.", details.Dependencies[0].Notes);
        Assert.All(details.Dependencies, item => Assert.True(item.HasPage));
        details.Dependencies[0].OpenPageCommand.Execute(null);
        Assert.Equal(new Uri("https://nexusmods.com/helldivers2/mods/100"), Assert.Single(f.BrowserRequests));
        var reverse = new ModDetailsViewModel(requirement, f.Services);
        Assert.Equal("Dependent", Assert.Single(reverse.Dependents).Name);
        Assert.False(reverse.HasDependencies); Assert.Equal("Dependency information unavailable. Refresh to check.", reverse.DependencySummary);
        var dialog = new ModDetailsDialog { DataContext = details };
        dialog.FindControl<TabControl>("DetailsTabs")!.SelectedIndex = 1;
        var window = new Window { Content = dialog }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(3, dialog.FindControl<ItemsControl>("DependenciesList")!.ItemCount);
            Assert.Equal(0, dialog.FindControl<ItemsControl>("DependentsList")!.ItemCount);
            dialog.DataContext = reverse; window.CaptureRenderedFrame()?.Dispose();
            Assert.Equal(1, dialog.FindControl<ItemsControl>("DependentsList")!.ItemCount);
        }
        finally { window.Close(); }
        await f.Services.Library.SetDependenciesAsync(requirement.Id, []);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        var knownEmpty = new ModDetailsViewModel(requirement, f.Services);
        Assert.Equal("All dependencies available.", knownEmpty.DependencySummary);
    }
}
