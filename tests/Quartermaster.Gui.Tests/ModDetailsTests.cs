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
        Assert.Equal(["In library", "Not in library", "External requirement"], details.Dependencies.Select(item => item.Status));
        Assert.Equal("Install this first.", details.Dependencies[0].Notes);
        Assert.All(details.Dependencies, item => Assert.True(item.HasPage));
        details.Dependencies[0].OpenPageCommand.Execute(null);
        Assert.Equal(new Uri("https://nexusmods.com/helldivers2/mods/100"), Assert.Single(f.BrowserRequests));
        var reverse = new ModDetailsViewModel(requirement, f.Services);
        Assert.Equal("Dependent", Assert.Single(reverse.Dependents).Name);
        Assert.False(reverse.HasDependencies); Assert.Equal("Dependency information unavailable.", reverse.DependencySummary);
        var dialog = new ModDetailsDialog { DataContext = details };
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
        Assert.Equal("No dependencies.", knownEmpty.DependencySummary);
    }
}
