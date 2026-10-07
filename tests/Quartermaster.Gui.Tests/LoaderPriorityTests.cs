using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public class LoaderPriorityTests
{
    [AvaloniaTheory]
    [InlineData(PriorityDirection.LastWins, false)]
    [InlineData(PriorityDirection.LastWins, true)]
    [InlineData(PriorityDirection.FirstWins, false)]
    [InlineData(PriorityDirection.FirstWins, true)]
    public async Task LoaderWarningTracksEffectivePriorityAndEnabledMods(PriorityDirection priority, bool renamed)
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source(renamed ? "Custom loader name" : "Bingus-Shared-Loader-v19"), CancellationToken.None);
        var loader = f.Services.Session.State.Mods.Single();
        if (renamed) await f.Services.Library.SetSourcesAsync(loader.Id, [new("nexusmods", "16292", "1")]);
        await f.Services.Session.ImportAsync(f.Source("Other mod", 2), CancellationToken.None);
        var other = f.Services.Session.State.Mods.Single(mod => mod.Id != loader.Id);
        var profile = ProfileEditor.Add(ProfileEditor.Add(f.Services.Session.ActiveProfile!, loader), other) with { Priority = priority };
        if (priority == PriorityDirection.FirstWins) profile = profile with { Entries = profile.Entries.Reverse().ToArray() };
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        var loaderRow = model.Entries.Single(row => row.Mod.Id == loader.Id);
        Assert.True(loaderRow.HasWarnings);
        Assert.Contains(priority == PriorityDirection.LastWins ? "bottom" : "top", loaderRow.WarningDescription!);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Border WarningIcon(Guid modId) => window.GetVisualDescendants().OfType<ModRowView>()
                .Single(view => view.DataContext is ProfileModItem row && row.Mod.Id == modId)
                .FindControl<Border>("ModWarning")!;
            Assert.True(WarningIcon(loader.Id).IsVisible);
            Assert.Equal(loaderRow.WarningDescription, ToolTip.GetTip(WarningIcon(loader.Id)));
            Assert.False(WarningIcon(other.Id).IsVisible);
            model.Search = "Other";
            Assert.True(loaderRow.HasWarnings);
            f.Dialogs.Confirm = false;
            await model.DeployCommand.ExecuteAsync();
            Assert.Contains("Bingus Shared Loader", f.Dialogs.Confirmations.Last().Message);
            model.Search = "";
            await model.MoveModAsync(loader.Id, other.Id, priority == PriorityDirection.LastWins);
            window.CaptureRenderedFrame()?.Dispose();
            Assert.Same(loaderRow, model.Entries.Single(row => row.Mod.Id == loader.Id));
            Assert.False(loaderRow.HasWarnings); Assert.False(WarningIcon(loader.Id).IsVisible);
            // A disabled mod after the loader does not affect its effective priority.
            var reordered = ProfileEditor.Move(model.SelectedProfile!, loader.Id, priority == PriorityDirection.LastWins ? 0 : 1);
            await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(reordered, other.Id, false), false, CancellationToken.None);
            Assert.False(loaderRow.HasWarnings);
            await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(reordered, loader.Id, false), false, CancellationToken.None);
            Assert.False(loaderRow.HasWarnings);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SimilarModNamesDoNotTriggerTheLoaderWarning()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        foreach (var name in new[] { "Bingus Shared Loader Helper", "Other mod" })
            await f.Services.Session.ImportAsync(f.Source(name, name == "Other mod" ? 2UL : 1UL), CancellationToken.None);
        var profile = f.Services.Session.ActiveProfile!;
        foreach (var mod in f.Services.Session.State.Mods) profile = ProfileEditor.Add(profile, mod);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        Assert.All(Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage).Entries, row => Assert.False(row.HasWarnings));
    }
}
