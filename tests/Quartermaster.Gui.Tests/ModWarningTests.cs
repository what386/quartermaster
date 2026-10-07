using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class ModWarningTests
{
    [AvaloniaFact]
    public async Task DisabledAndRemovedDependenciesWarnAndRecoverWithoutReplacingRows()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Requirement", 1), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Dependent", 2), CancellationToken.None);
        var requirement = f.Services.Session.State.Mods.Single(mod => mod.Name == "Requirement");
        var dependent = f.Services.Session.State.Mods.Single(mod => mod.Name == "Dependent");
        await f.Services.Library.SetSourcesAsync(requirement.Id, [new("nexusmods", "100", "1")]);
        await f.Services.Library.SetDependenciesAsync(dependent.Id, [new("Requirement", "https://www.nexusmods.com/helldivers2/mods/100")]);
        var profile = ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, requirement), dependent);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        var row = model.Entries.Single(row => row.Mod.Id == dependent.Id);
        Assert.False(row.HasWarnings);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(profile, requirement.Id, false), false, CancellationToken.None);
        Assert.Same(row, model.Entries.Single(row => row.Mod.Id == dependent.Id));
        Assert.Contains("Requires Requirement (disabled)", row.WarningDescription);
        Assert.Contains("Required by enabled mod Dependent", model.Entries.Single(row => row.Mod.Id == requirement.Id).WarningDescription);
        // Filtering must not remove warnings from the deployment confirmation.
        model.Search = "nothing visible"; f.Dialogs.Confirm = false;
        await model.DeployCommand.ExecuteAsync();
        Assert.Contains(row.WarningDescription, f.Dialogs.Confirmations.Last().Message);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        Assert.False(row.HasWarnings);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.Remove(profile, requirement.Id), false, CancellationToken.None);
        Assert.Contains("missing from this profile", row.WarningDescription);
        // Metadata survives reopening the library, without a provider request.
        await using var reopened = f.ReopenServices(); await reopened.Session.InitializeAsync(CancellationToken.None);
        Assert.Equal("Requirement", Assert.Single(reopened.Session.State.Mods.Single(mod => mod.Id == dependent.Id).Dependencies).Name);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(model.SelectedProfile!, dependent.Id, false), false, CancellationToken.None);
        Assert.False(row.HasWarnings);
    }

    [AvaloniaFact]
    public async Task ClashesAndDependencyWarningsShareOneIconTooltipAndDeploySummary()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("First"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Winner"), CancellationToken.None);
        var first = f.Services.Session.State.Mods.Single(mod => mod.Name == "First");
        var winner = f.Services.Session.State.Mods.Single(mod => mod.Name == "Winner");
        await f.Services.Library.SetDependenciesAsync(first.Id, [new("Missing", "https://www.nexusmods.com/helldivers2/mods/200")]);
        await f.Services.Session.SaveProfileAsync(ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, first), winner), false, CancellationToken.None);
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        var row = model.Entries.Single(row => row.Mod.Id == first.Id);
        Assert.Contains("Requires Missing", row.WarningDescription);
        Assert.Contains("Clashes with Winner", row.WarningDescription);
        Assert.Contains("Winner wins", row.WarningDescription);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var view = window.GetVisualDescendants().OfType<ModRowView>().Single(view => ReferenceEquals(view.DataContext, row));
            var icon = view.FindControl<Border>("ModWarning")!;
            Assert.True(icon.IsVisible); Assert.Equal(row.WarningDescription, ToolTip.GetTip(icon));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "CLASH");
            f.Dialogs.Confirm = false; await model.DeployCommand.ExecuteAsync();
            Assert.Contains("Requires Missing", f.Dialogs.Confirmations.Last().Message);
            Assert.Contains("Clashes with Winner", f.Dialogs.Confirmations.Last().Message);
            await model.MoveModAsync(first.Id, winner.Id, true);
            Assert.Contains("First wins", row.WarningDescription);
            Assert.Equal(row.WarningDescription, ToolTip.GetTip(icon));
            await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(model.SelectedProfile!, winner.Id, false), false, CancellationToken.None);
            Assert.DoesNotContain("Clashes", row.WarningDescription); Assert.True(row.HasWarnings);
            f.Shell.Navigate(PageKind.Mods);
            var library = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
            Assert.Contains("missing from your library", library.Mods.Single(mod => mod.Mod.Id == first.Id).WarningDescription);
        }
        finally { window.Close(); }
    }
}
