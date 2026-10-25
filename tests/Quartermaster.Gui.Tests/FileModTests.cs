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
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeWorksWithLinkedGameAndAppDataRoots(bool hasFileMods)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); using var config = new ConfigFixture();
        var target = GameConfig(f, config);
        var data = Path.Combine(f.Root, "physical-data"); Directory.CreateDirectory(data);
        var dataAlias = Path.Combine(f.Root, "data-alias"); Directory.CreateSymbolicLink(dataAlias, data);
        var gameAlias = Path.Combine(f.Root, "game-parent-alias"); Directory.CreateSymbolicLink(gameAlias, f.Root);
        await using var services = new Quartermaster.Gui.Services.AppServices(Path.Combine(dataAlias, "Quartermaster"), f.Dialogs, () => []);
        await new Quartermaster.Gui.Services.SettingsStore(services.DataDirectory).SaveAsync(new()
        { GameDataDirectory = Path.Combine(gameAlias, "game") }, CancellationToken.None);
        await services.Session.InitializeAsync(CancellationToken.None);
        Assert.Equal("", services.Session.DeploymentProblem);
        if (hasFileMods)
        {
            await services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
            await services.Session.AddModToProfileAsync(services.Session.State.Mods.Single().Id, services.Session.ActiveProfile!.Id, CancellationToken.None);
            await services.Session.DeployAsync(services.Session.ActiveProfile!.Id, f.Dialogs, CancellationToken.None);
            Assert.Equal(200u, ReadValue(target));
        }
        var patch = Path.Combine(f.Game, Fixture.Archive + ".patch_99"); await File.WriteAllBytesAsync(patch, [1]);
        await services.Session.PurgeAsync(CancellationToken.None);
        Assert.False(File.Exists(patch)); Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
        Assert.True(services.Session.FileModHealth.Complete); Assert.Equal("", services.Session.DeploymentProblem);
    }

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
        Assert.Contains("file-mod options changed", await f.Services.Session.GetLaunchWarningAsync(profileId, CancellationToken.None));
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
    public async Task ConfigDamageAndOlderReceiptsHaveSpecificDeploymentMessages()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        var mod = f.Services.Session.State.Mods.Single(); var profileId = f.Services.Session.ActiveProfile!.Id;
        await f.Services.Session.AddModToProfileAsync(mod.Id, profileId, CancellationToken.None);
        await f.Services.Session.DeployAsync(profileId, f.Dialogs, CancellationToken.None);
        var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage); var row = vm.Entries.Single();
        await File.WriteAllBytesAsync(target, [0]); await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Contains("Merged config changed", row.DeploymentDescription);
        Assert.Contains(ConfigFixture.Target, row.DeploymentDescription);
        var warning = await f.Services.Session.GetLaunchWarningAsync(profileId, CancellationToken.None);
        Assert.Contains("Water", warning); Assert.Contains(ConfigFixture.Target, warning);
        File.Delete(target); await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Contains("Missing merged config", row.DeploymentDescription);
        Assert.Contains("Steam", row.DeploymentDescription);
        await File.WriteAllBytesAsync(target, config.Stock);
        await f.Services.Session.DeployAsync(profileId, f.Dialogs, CancellationToken.None);
        var receiptPath = Directory.GetFiles(Path.Combine(f.Data, "filemod-receipts"), "*.json").Single();
        var receipt = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(receiptPath))!;
        receipt.AsObject().Remove("selection"); await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        vm.SelectedMod = row; vm.Options!.Options[0].ChoiceIndex = 1; await vm.ApplyOptionsCommand.ExecuteAsync();
        Assert.Contains("older deployment record has no option or order details", row.DeploymentDescription);
        Assert.DoesNotContain("File mods not included", row.DeploymentDescription);
        // Invalid new metadata must be reported as an inspection error, not silently treated as undeployed.
        receipt["selection"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        { ["modId"] = Guid.Empty.ToString(), ["sourceHash"] = "invalid" });
        await File.WriteAllTextAsync(receiptPath, receipt.ToJsonString());
        await f.Services.Session.ReloadAsync(CancellationToken.None);
        Assert.Contains("could not be inspected", row.DeploymentDescription);
        Assert.Contains("Invalid file-mod deployment receipt", row.DeploymentDescription);
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
        var review = Assert.Single(f.Dialogs.FileMergeReviews);
        var group = Assert.Single(review.Groups);
        Assert.Equal("generated_entities.dl_bin", group.FileName);
        var item = Assert.Single(group.Items);
        Assert.Contains("Sliding", item.Winner);
        Assert.Contains("Vaulting", Assert.Single(item.OtherMods).Name);
        Assert.Equal(1, item.ValueCount);
        Assert.False(item.WholeFile);
        f.Dialogs.Confirm = true; await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Equal(300u, ReadValue(target)); Assert.True(f.Services.Session.HasFileModWarnings);
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        Assert.All(vm.Entries, row => { Assert.False(row.HasWarnings); Assert.Contains("Applied mod:\n  Sliding", row.ErrorDescription); Assert.True(row.HasErrors); });
        await using (var reopened = f.ReopenServices())
        {
            await reopened.Session.InitializeAsync(CancellationToken.None);
            Assert.Equal(2, reopened.Session.GetModErrors().Count);
        }
        await vm.MoveModAsync(profile.Entries[1].ModId, profile.Entries[0].ModId, false);
        Assert.All(vm.Entries, row => { Assert.False(row.HasWarnings); Assert.False(row.HasErrors); });
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Equal(200u, ReadValue(target)); Assert.Equal(0, f.Launches);
    }

    [AvaloniaFact]
    public async Task PreviewUpdatesWarningsForToggleOrderAndPriorityWithoutApplyingFiles()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        foreach (var (name, value) in new[] { ("Vaulting", 200u), ("Sliding", 300u) })
        {
            var directory = Path.Combine(f.Root, name); Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "generated_entities.dl_bin"), config.Mod(name, 0, value).Content.ToArray());
            await f.Services.Session.ImportAsync(directory, CancellationToken.None);
            var mod = f.Services.Session.State.Mods.Single(mod => mod.Name == name);
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        }
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row => { Assert.True(row.HasErrors); Assert.False(row.HasWarnings); Assert.Contains("Applied mod:\n  Sliding", row.ErrorDescription); Assert.Contains("Overridden mods:\n  Vaulting", row.ErrorDescription); Assert.DoesNotContain("conflicting values", row.ErrorDescription); });
        var sliding = vm.Entries.Single(row => row.Name == "Sliding");
        var vaulting = vm.Entries.Single(row => row.Name == "Vaulting");
        await vm.MoveModAsync(sliding.Mod.Id, vaulting.Mod.Id, false);
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row => Assert.Contains("Applied mod:\n  Vaulting", row.ErrorDescription));
        await f.Services.Session.SaveProfileAsync(vm.SelectedProfile! with { Priority = PriorityDirection.FirstWins }, false, CancellationToken.None);
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row => Assert.Contains("Applied mod:\n  Sliding", row.ErrorDescription));
        vm.SelectedMod = sliding;
        await vm.ToggleCommand.ExecuteAsync();
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row => { Assert.False(row.HasWarnings); Assert.False(row.HasErrors); });
        // Rapid edits must not allow an older winner to repopulate the current tooltips.
        await vm.ToggleCommand.ExecuteAsync();
        await vm.ToggleCommand.ExecuteAsync();
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row => { Assert.False(row.HasWarnings); Assert.False(row.HasErrors); });
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
        Assert.Empty(f.Dialogs.FileMergeReviews);
        Assert.False(Directory.Exists(Path.Combine(f.Data, "filemod-receipts")));
        Assert.Empty(Directory.GetFiles(f.Game, "*.patch_*"));
        Assert.Equal(0, f.Launches);
    }

    [AvaloniaFact]
    public async Task PreviewFollowsOptionChangesAndKeepsTheViewedInactiveProfile()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        var directory = Path.Combine(f.Root, "Override"); Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(Path.Combine(directory, "generated_entities.dl_bin"), config.Mod("Override", 0, 300).Content.ToArray());
        await f.Services.Session.ImportAsync(directory, CancellationToken.None);
        var other = ProfileEditor.Create("Other loadout");
        foreach (var mod in f.Services.Session.State.Mods) other = ProfileEditor.Add(other, mod);
        await f.Services.Session.SaveProfileAsync(other, false, CancellationToken.None);
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        vm.SelectedProfile = f.Services.Session.State.Profiles.Single(profile => profile.Id == other.Id);
        vm.SelectedMod = vm.Entries.Single(row => row.Name == "Water");
        var draft = vm.Options!; draft.Options[0].ChoiceIndex = 1;
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.Equal(other.Id, vm.SelectedProfile!.Id);
        Assert.Same(draft, vm.Options); // Diagnostic notifications must not discard unsaved option edits.
        Assert.Equal(1, vm.Options!.Options[0].ChoiceIndex);
        Assert.All(vm.Entries, row => { Assert.True(row.HasErrors); Assert.False(row.HasWarnings); });
        Assert.Empty(f.Services.Session.GetModErrors()); // The active profile has no file mods.
        await vm.ApplyOptionsCommand.ExecuteAsync();
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.Equal(other.Id, vm.SelectedProfile!.Id);
        Assert.All(vm.Entries, row => { Assert.False(row.HasWarnings); Assert.False(row.HasErrors); }); // Both now request 300.
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
    }

    [AvaloniaFact]
    public async Task WholeFileReplacementShowsSkippedModsAsErrorsWithWrappedSections()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        foreach (var name in new[] { "Water", "Vaulting", "Replacement" })
        {
            var directory = Path.Combine(f.Root, name); Directory.CreateDirectory(directory);
            var content = name == "Replacement"
                ? GameConfigCodec.EncryptPreservingHash(ConfigFixture.Config(68), ConfigHash.Compute(config.Plain))
                : config.Mod(name, 0, 200).Content.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(directory, "generated_entities.dl_bin"), content);
            await f.Services.Session.ImportAsync(directory, CancellationToken.None);
            var mod = f.Services.Session.State.Mods.Single(mod => mod.Name == name);
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        }
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.All(vm.Entries, row =>
        {
            Assert.True(row.HasErrors); Assert.False(row.HasWarnings);
            Assert.Contains("Config file:\n  " + ConfigFixture.Target, row.ErrorDescription);
            Assert.Contains("Applied mod:\n  Replacement", row.ErrorDescription);
            Assert.Contains("Skipped mods:\n  Water", row.ErrorDescription);
            Assert.Contains("\n  Vaulting", row.ErrorDescription);
            Assert.Contains("all edits from the skipped mods are lost", row.ErrorDescription);
            Assert.DoesNotContain("conflicting values", row.ErrorDescription);
        });
        var view = new ModRowView { DataContext = vm.Entries[0] };
        var window = new Window { Content = view }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.False(view.FindControl<Border>("ModWarning")!.IsVisible);
            var icon = view.FindControl<Border>("ModError")!;
            Assert.True(icon.IsVisible);
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(icon));
            Assert.Equal(vm.Entries[0].ErrorDescription, tip.Text);
            Assert.Equal(Avalonia.Media.TextWrapping.Wrap, tip.TextWrapping);
            Assert.Equal(440, tip.MaxWidth);
        }
        finally { window.Close(); }
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
    }

    [AvaloniaFact]
    public async Task PreviewShowsCompatibilityErrorsBeforeApplyAndReloadRechecksGameFiles()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        await File.WriteAllBytesAsync(target, GameConfigCodec.Encrypt(ConfigFixture.Config(firstValue: 900)));
        foreach (var mod in f.Services.Session.State.Mods)
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        f.Shell.Navigate(PageKind.Profiles); var vm = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        await f.Services.Session.WaitForFileModPreviewsAsync();
        var failed = vm.Entries.Single(row => row.Name == "Water");
        Assert.True(failed.HasErrors);
        Assert.Contains("Needs a new version", failed.ErrorDescription);
        Assert.False(vm.Entries.Single(row => row.Name == "Armor").HasErrors);
        Assert.False(f.Services.Operations.IsError); // Background checks do not interrupt editing with an error toast.
        var view = new ModRowView { DataContext = failed }; var window = new Window { Content = view }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            Assert.True(view.FindControl<Border>("ModError")!.IsVisible);
            await File.WriteAllBytesAsync(target, config.Stock);
            await f.Services.Session.ReloadAsync(CancellationToken.None);
            await f.Services.Session.WaitForFileModPreviewsAsync();
            Assert.False(failed.HasErrors);
            Assert.False(view.FindControl<Border>("ModError")!.IsVisible);
        }
        finally { window.Close(); }
        Assert.False(Directory.Exists(Path.Combine(f.Data, "filemod-receipts")));
        Assert.Empty(f.Dialogs.FileMergeReviews);
        Assert.Equal(config.Stock, await File.ReadAllBytesAsync(target));
    }

    [AvaloniaFact]
    public async Task OutdatedFileModErrorIsAttributedAndSuccessfulRetryClearsIt()
    {
        using var f = new Fixture(); using var config = new ConfigFixture();
        await f.Shell.InitializeAsync(); var target = GameConfig(f, config);
        await f.Services.Session.ImportAsync(Package(f, config, "Water"), CancellationToken.None);
        await f.Services.Session.ImportAsync(f.Source("Armor"), CancellationToken.None);
        foreach (var mod in f.Services.Session.State.Mods)
            await f.Services.Session.AddModToProfileAsync(mod.Id, f.Services.Session.ActiveProfile!.Id, CancellationToken.None);
        await f.Services.Session.SetRepatchModeAsync(Quartermaster.Gui.Services.RepatchMode.Never, CancellationToken.None);
        await File.WriteAllBytesAsync(target, GameConfigCodec.Encrypt(ConfigFixture.Config(firstValue: 900)));
        var profile = f.Services.Session.ActiveProfile!;
        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None));
        Assert.Contains("Water", failure.Message);
        var errors = f.Services.Session.GetModErrors();
        var modError = Assert.Single(errors);
        Assert.Equal(f.Services.Session.State.Mods.Single(mod => mod.Name == "Water").Id, modError.Key);
        Assert.Contains("Needs a new version", modError.Value);
        f.Shell.Navigate(PageKind.Mods);
        var library = Assert.IsType<ModsViewModel>(f.Shell.CurrentPage);
        Assert.True(library.Mods.Single(mod => mod.Name == "Water").HasErrors);
        Assert.False(library.Mods.Single(mod => mod.Name == "Armor").HasErrors);
        await File.WriteAllBytesAsync(target, config.Stock);
        await f.Services.Session.DeployAsync(profile.Id, f.Dialogs, CancellationToken.None);
        Assert.Empty(f.Services.Session.GetModErrors());
        Assert.All(library.Mods, row => Assert.False(row.HasErrors));
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
        var model = Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage);
        var failed = model.Entries.Single(row => row.Mod.Id == armor.Id);
        Assert.True(failed.HasErrors);
        Assert.Contains("Armor", failed.ErrorDescription);
        Assert.False(model.Entries.Single(row => row.Mod.Id != armor.Id).HasErrors);
        var view = new ModRowView { DataContext = failed };
        var window = new Window { Content = view }; window.Show();
        try
        {
            window.CaptureRenderedFrame()?.Dispose();
            var icon = view.FindControl<Border>("ModError")!;
            Assert.True(icon.IsVisible);
            Assert.Equal(failed.ErrorDescription, Assert.IsType<TextBlock>(ToolTip.GetTip(icon)).Text);
            await f.Services.Session.SaveProfileAsync(ProfileEditor.SetEnabled(model.SelectedProfile!, armor.Id, false), false, CancellationToken.None);
            Assert.False(failed.HasErrors);
            Assert.False(icon.IsVisible);
        }
        finally { window.Close(); }
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
        await f.Services.Session.WaitForFileModPreviewsAsync();
        Assert.Empty(f.Services.Session.GetModErrors());
        // Apply must validate again even if the last background preview succeeded.
        await File.WriteAllBytesAsync(target, GameConfigCodec.Encrypt(ConfigFixture.Config(firstValue: 900)));
        f.Shell.Navigate(PageKind.Profiles); await Assert.IsType<ProfilesViewModel>(f.Shell.CurrentPage).DeployCommand.ExecuteAsync();
        Assert.True(f.Services.Operations.IsError); Assert.Contains("Outdated water", f.Services.Operations.Message);
        Assert.Empty(Directory.GetFiles(f.Game, "*.patch_*")); Assert.Equal(0, f.Launches);
    }
    private static uint ReadValue(string path) => BinaryPrimitives.ReadUInt32LittleEndian(GameConfigCodec.Decrypt(File.ReadAllBytes(path)).AsSpan(28));
}
