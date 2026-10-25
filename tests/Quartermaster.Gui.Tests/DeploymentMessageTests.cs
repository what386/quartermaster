using Avalonia.Headless.XUnit;
using Quartermaster.Gui.Profiles;
using Quartermaster.Gui.Services;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class DeploymentMessageTests
{
    [AvaloniaFact]
    public async Task MissingAndModifiedFilesNameTheModAndFileInTooltipsAndLaunchWarnings()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.Source("Cape"), CancellationToken.None);
        var mod = f.Services.Session.State.Mods.Single(); var profile = f.Services.Session.ActiveProfile!;
        await f.Services.Session.AddModToProfileAsync(mod.Id, profile.Id, CancellationToken.None);
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        var row = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage).Entries.Single();
        var owned = f.Services.Session.Inspection!.Ledger.Files.First(); var path = Path.Combine(f.Game, owned.Name);
        await File.WriteAllBytesAsync(path, [0]);
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Contains("changed outside Quartermaster", row.DeploymentDescription);
        Assert.Contains("Cape", row.DeploymentDescription); Assert.Contains(owned.Name, row.DeploymentDescription);
        var warning = await f.Services.Session.GetLaunchWarningAsync(profile.Id, CancellationToken.None);
        Assert.Contains("Cape", warning); Assert.Contains(owned.Name, warning); Assert.Contains("modified", warning);
        File.Delete(path); await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Contains("Missing deployed file", row.DeploymentDescription);
        warning = await f.Services.Session.GetLaunchWarningAsync(profile.Id, CancellationToken.None);
        Assert.Contains("missing", warning); Assert.Contains(owned.Name, warning);
    }

    [AvaloniaFact]
    public async Task OptionsAndEffectiveArchiveOrderHaveDifferentDeploymentExplanations()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(f.OptionsSource(), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Cape", 99), CancellationToken.None);
        foreach (var mod in f.Services.Session.State.Mods)
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        await f.Services.Session.SetRepatchModeAsync(RepatchMode.Never, CancellationToken.None);
        var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await f.Services.Session.DeployAsync(vm.SelectedProfile!.Id, f.Dialogs, CancellationToken.None);
        vm.SelectedMod = vm.Entries.Single(row => row.Name == "Armor variants");
        vm.Options!.Options[0].ChoiceIndex = 1; await vm.ApplyOptionsCommand.ExecuteAsync();
        Assert.Contains("options changed", vm.SelectedMod!.DeploymentDescription);
        await f.Services.Session.DeployAsync(vm.SelectedProfile!.Id, f.Dialogs, CancellationToken.None);
        await vm.MoveModAsync(vm.Entries[1].Mod.Id, vm.Entries[0].Mod.Id, false);
        Assert.All(vm.Entries, row =>
        {
            Assert.Equal(ModDeploymentState.Warning, row.DeploymentState);
            Assert.Contains("archive priority changed", row.DeploymentDescription);
            Assert.DoesNotContain("options changed", row.DeploymentDescription);
        });
        var warning = await f.Services.Session.GetLaunchWarningAsync(vm.SelectedProfile!.Id, CancellationToken.None);
        Assert.Contains("Armor variants", warning); Assert.Contains("Cape", warning); Assert.Contains("archive priority changed", warning);
    }

    [AvaloniaFact]
    public async Task UnmanagedPatchesAndUnreadableRecordsExplainWhatCouldNotBeVerified()
    {
        using var f = new Fixture(); await f.Shell.InitializeAsync(); var profile = f.Services.Session.ActiveProfile!;
        var name = Fixture.Archive + ".patch_19"; await File.WriteAllBytesAsync(Path.Combine(f.Game, name), [0]);
        var warning = await f.Services.Session.GetLaunchWarningAsync(profile.Id, CancellationToken.None);
        Assert.Contains("Unmanaged archive patches", warning); Assert.Contains(name, warning);
        Assert.Contains("cannot identify the mods", warning);
        File.Delete(Path.Combine(f.Game, name));
        await File.WriteAllTextAsync(Path.Combine(f.Data, "deployment.lock"), "{broken");
        warning = await f.Services.Session.GetLaunchWarningAsync(profile.Id, CancellationToken.None);
        Assert.Contains("record is unreadable or invalid", warning); Assert.Contains("Purge patches", warning);
    }
}
