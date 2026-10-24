using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Quartermaster.Gui.Mods;
using Quartermaster.Gui.Profiles;
using Quartermaster.FileMerger;
using Quartermaster.Library.Profiles;
using ConfigFixture = Quartermaster.FileMerger.Tests.Fixture;
using Xunit;

namespace Quartermaster.Gui.Tests;

public sealed class FileModTests
{
    private static string Package(Fixture f, ConfigFixture config, string name)
    {
        var folder = Path.Combine(f.Root, name);
        foreach (var (variant, value) in new[] { ("Water", 200u), ("WaterVault", 300u) })
        {
            var directory = Path.Combine(folder, "Install", variant); Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "b5b0c0ffee5b5b11.patch_0"), ConfigFixture.Patch(config.Payload(config.Mod(variant, 0, value))));
        }
        foreach (var directory in new[] { "Install", "UninstallHelper" })
        {
            Directory.CreateDirectory(Path.Combine(folder, directory));
            File.WriteAllBytes(Path.Combine(folder, directory, Fixture.Archive + ".patch_0"), ConfigFixture.Patch(Encoding.UTF8.GetBytes("-- HD2 file-mod installer 1 (shared)")));
        }
        File.WriteAllText(Path.Combine(folder, "manifest.json"), """
            {"Options":[{"Name":"Water","Include":["Install"],"SubOptions":[
            {"Name":"Water only","Include":["Install/Water"]},{"Name":"Water and vaulting","Include":["Install/WaterVault"]}]},
            {"Name":"Uninstall helper","Include":["UninstallHelper"]}]}
            """);
        return folder;
    }
    private static string GameConfig(Fixture f, ConfigFixture config)
    {
        var target = Path.Combine(f.Root, ConfigFixture.Target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, config.Stock); return target;
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileBadgeAppearsInLibraryAndLoadoutWhileLuaHelperAndUninstallOptionAreHidden(bool library)
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync();
        await f.Services.Session.ImportAsync(Package(f, config, "Water mod"), CancellationToken.None);
        var mod = f.Services.Session.State.Mods.Single();
        await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        f.Shell.Navigate(library ? PageKind.Mods : PageKind.Profiles);
        var window = new MainWindow { DataContext = f.Shell }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var row = Assert.Single(window.GetVisualDescendants().OfType<ModRowView>());
            Assert.True(row.FindControl<Border>("FileModIcon")!.IsVisible);
            Assert.False(row.FindControl<Border>("LuaIcon")!.IsVisible);
            Assert.Equal("File mod — merged into game configs", ToolTip.GetTip(row.FindControl<Border>("FileModIcon")!));
            var options = new ModOptionsViewModel(mod, []);
            Assert.Equal("Water", Assert.Single(options.Options).Name);
            Assert.Equal(new[] { "Water only", "Water and vaulting" }, options.Options[0].Choices.Select(choice => choice.Name));
            Assert.True(new ModDetailsViewModel(mod, f.Services).IsFileBased);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DeployUsesSelectedVariantAndDisablingOrPurgingRestoresStockWithoutLaunching()
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync();
        var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water mod"), CancellationToken.None);
        var mod = f.Services.Session.State.Mods.Single(); var profileId = f.Services.Session.ActiveProfile!.Id;
        await f.Services.Session.AddModToProfileAsync(mod.Id, profileId, CancellationToken.None);
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await vm.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(200u, ReadValue(target)); Assert.Empty(f.Services.Session.Inspection!.Ledger.Files);
        Assert.True(vm.SelectedMod!.IsLoaded); Assert.Null(await f.Services.Session.GetLaunchWarningAsync(profileId, CancellationToken.None));
        vm.Options!.Options[0].ChoiceIndex = 1; await vm.ApplyOptionsCommand.ExecuteAsync();
        Assert.Contains("file mods", await f.Services.Session.GetLaunchWarningAsync(profileId, CancellationToken.None));
        Assert.True(vm.SelectedMod!.HasDeploymentWarning);
        await vm.DeployCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(300u, ReadValue(target));
        await vm.ToggleCommand.ExecuteAsync(); await vm.DeployCommand.ExecuteAsync();
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target)); Assert.True(vm.SelectedMod!.IsUnloaded);
        await vm.ToggleCommand.ExecuteAsync(); await vm.DeployCommand.ExecuteAsync();
        Assert.Equal(300u, ReadValue(target));
        await vm.PurgeCommand.ExecuteAsync(); Assert.False(f.Services.Operations.IsError, f.Services.Operations.Message);
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target)); Assert.Equal(0, f.Launches);
    }

    [AvaloniaFact]
    public async Task ConflictsAreReviewedAndReorderingChangesTheWinningFileMod()
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        var sources = new[] { ("Vaulting", 200u), ("Sliding", 300u) };
        foreach (var (name, value) in sources)
        {
            var directory = Path.Combine(f.Root, name); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "generated_entities.dl_bin"), config.Mod(name, 0, value).Content.ToArray());
            await f.Services.Session.ImportAsync(directory, CancellationToken.None);
            var mod = f.Services.Session.State.Mods.Single(mod => mod.Name == name);
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        }
        var profile = f.Services.Session.ActiveProfile!; f.Dialogs.Confirm = false;
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
        var review = Assert.Single(f.Dialogs.Confirmations);
        Assert.Equal("File mod conflicts", review.Title); Assert.Contains("Vaulting", review.Message); Assert.Contains("Sliding", review.Message);
        f.Dialogs.Confirm = true; await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Equal(300u, ReadValue(target)); Assert.True(f.Services.Session.HasFileModWarnings);
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await vm.MoveModAsync(profile.Entries[1].ModId, profile.Entries[0].ModId, false);
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Equal(200u, ReadValue(target)); Assert.Equal(0, f.Launches);
    }

    [AvaloniaFact]
    public async Task RedeployCanClearAnInterruptedReceiptAfterConfigsWereAlreadyRestored()
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync(); GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        var mod = f.Services.Session.State.Mods.Single(); var profile = f.Services.Session.ActiveProfile!;
        await f.Services.Session.AddModToProfileAsync(mod.Id, profile.Id, CancellationToken.None);
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        await f.Services.Session.PurgeAsync(CancellationToken.None);
        var path = Directory.GetFiles(Path.Combine(f.Data, "filemod-receipts"), "*.json").Single();
        var receipt = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!; receipt["complete"] = false;
        await File.WriteAllTextAsync(path, receipt.ToJsonString());
        profile = ProfileEditor.SetEnabled(f.Services.Session.ActiveProfile!, mod.Id, false);
        await f.Services.Session.SaveProfileAsync(profile, false, CancellationToken.None);
        Assert.False(f.Services.Session.FileModHealth.Complete);
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.True(f.Services.Session.FileModHealth.Complete);
        Assert.Null(await f.Services.Session.GetLaunchWarningAsync(profile.Id, CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task OrdinaryPatchFailureLeavesFileModsUnpublishedAndMarksTheDeploymentIncomplete()
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        foreach (var mod in f.Services.Session.State.Mods)
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        await f.Services.Session.SetRepatchModeAsync(Quartermaster.Gui.Services.RepatchMode.Never, CancellationToken.None);
        var armor = f.Services.Session.State.Mods.Single(mod => mod.Name == "Armor");
        var stored = armor.PatchSets[0].Files.Single(file => file.Kind == Quartermaster.Core.Patching.PatchFileKind.Main);
        await File.WriteAllBytesAsync(Path.Combine(f.Data, "library", armor.Id.ToString("N"), stored.RelativePath), [0]);
        f.Shell.Navigate(PageKind.Profiles); await Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage).DeployCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.False(f.Services.Session.FileModHealth.Complete);
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target)); Assert.Equal(0, f.Launches);
        await f.Services.Session.PurgeAsync(CancellationToken.None); Assert.True(f.Services.Session.FileModHealth.Complete);
    }

    [AvaloniaFact]
    public async Task OldFileModVersionsFailBeforeRegularPatchesArePublished()
    {
        using var f = new Fixture(); using var config = new ConfigFixture(); await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Outdated water"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        foreach (var mod in f.Services.Session.State.Mods)
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        await File.WriteAllBytesAsync(target, GameConfigCodec.Encrypt(ConfigFixture.Config(firstValue: 900)));
        f.Shell.Navigate(PageKind.Profiles); await Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage).DeployCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Contains("Outdated water", f.Services.Operations.Message);
        Assert.Empty(Directory.GetFiles(f.Game, "*.patch_*")); Assert.Equal(0, f.Launches);
    }
    private static uint ReadValue(string path) => BinaryPrimitives.ReadUInt32LittleEndian(GameConfigCodec.Decrypt(File.ReadAllBytes(path)).AsSpan(28));
}
