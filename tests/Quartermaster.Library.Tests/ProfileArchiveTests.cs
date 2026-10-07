using global::System.IO.Compression;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Library.Tests;

public class ProfileArchiveTests
{
    [Fact]
    public async Task RoundTripIncludesAllFilesAndRemapsExistingModsOptionsAndGroups()
    {
        using var sender = new Fixture(); using var receiver = new Fixture();
        var source = sender.Source("Variants/common", 1);
        sender.Source("Variants/blue", 2); sender.Source("Variants/red", 3);
        source = Path.GetDirectoryName(source)!;
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
            {"Version":1,"Name":"Variants","ModVersion":"v2","IconPath":"icon.png","Options":[
              {"Name":"Color","SubOptions":[{"Name":"Blue","Include":["blue"]},{"Name":"Red","Include":["red"]}]},
              {"Name":"Common","Include":["common"]}]}
            """);
        await File.WriteAllBytesAsync(Path.Combine(source, "icon.png"), [1, 2, 3]);
        await File.WriteAllTextAsync(Path.Combine(source, "readme.txt"), "Included with the mod.");
        var variants = await sender.Library.ImportAsync(source, "Custom mod name", installedAsDependency: true);
        var disabled = await sender.Library.ImportAsync(sender.Source("Disabled", 4), installedAsDependency: true);
        await sender.Library.SetDependenciesAsync(variants.Id, [new("Requirement", "https://www.nexusmods.com/helldivers2/mods/100")]);
        await sender.Library.SetDependenciesAsync(disabled.Id, []);
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("My loadout"), disabled), variants);
        profile = ProfileEditor.SetEnabled(profile, disabled.Id, false);
        profile = ProfileEditor.SetOptions(profile, variants, [new(variants.Options[0].Id, true, 1), new(variants.Options[1].Id, false)]);
        profile = ProfileEditor.AddGroup(profile, "Equipment", [variants.Id]);
        profile = ProfileEditor.SetGroupExpanded(profile, profile.Groups[0].Id, false);
        profile = ProfileEditor.AddGroup(profile, "Empty group") with { Priority = PriorityDirection.FirstWins };
        await sender.Library.SaveProfileAsync(profile, true);
        var destination = Path.Combine(sender.Root, "loadout.zip");
        await new ProfileArchives(sender.Store, sender.Contents).ExportAsync(profile.Id, destination);
        using (var zip = ZipFile.OpenRead(destination))
        {
            Assert.Contains(zip.Entries, entry => entry.FullName == "profile.json");
            Assert.Contains(zip.Entries, entry => entry.FullName == $"mods/{variants.Id:N}/icon.png");
            Assert.Contains(zip.Entries, entry => entry.FullName == $"mods/{variants.Id:N}/red/{Fixture.Archive}.patch_7");
            Assert.Contains(zip.Entries, entry => entry.FullName == $"mods/{disabled.Id:N}/{Fixture.Archive}.patch_7");
        }
        var existing = await receiver.Library.ImportAsync(source);
        var active = ProfileEditor.Create("Existing profile"); await receiver.Library.SaveProfileAsync(active, true);
        var imported = await new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination);
        var state = await receiver.Library.LoadAsync();
        Assert.NotEqual(profile.Id, imported.Id); Assert.Equal(profile.Name, imported.Name); Assert.Equal(profile.Priority, imported.Priority);
        Assert.Equal(active.Id, state.ActiveProfileId); Assert.Equal(2, state.Mods.Count); Assert.Equal(2, state.Profiles.Count);
        Assert.False(imported.Entries[0].Enabled); Assert.Equal(existing.Id, imported.Entries[1].ModId);
        Assert.Equal(new[] { "Equipment", "Empty group" }, imported.Groups.Select(group => group.Name));
        Assert.False(imported.Groups[0].IsExpanded); Assert.NotEqual(profile.Groups[0].Id, imported.Groups[0].Id);
        Assert.Equal(imported.Groups[0].Id, imported.Entries[1].GroupId);
        Assert.Equal(existing.Options[0].Id, imported.Entries[1].Options[0].OptionId);
        Assert.Equal(1, imported.Entries[1].Options[0].ChoiceIndex); Assert.False(imported.Entries[1].Options[1].Enabled);
        Assert.Equal("red", Assert.Single(PatchSelection.Select(existing, imported.Entries[1])).Folder);
        var importedDisabled = state.Mods.Single(mod => mod.Id == imported.Entries[0].ModId);
        Assert.True(state.Mods.Single(mod => mod.Id == existing.Id).DependenciesKnown);
        Assert.Equal("Requirement", Assert.Single(state.Mods.Single(mod => mod.Id == existing.Id).Dependencies).Name);
        Assert.True(importedDisabled.DependenciesKnown); Assert.Empty(importedDisabled.Dependencies);
        Assert.True(importedDisabled.InstalledAsDependency);
        Assert.False(state.Mods.Single(mod => mod.Id == existing.Id).InstalledAsDependency);
        Assert.Equal("Disabled", importedDisabled.Name);
        foreach (var file in Directory.GetFiles(sender.Contents.GetModDirectory(disabled.Id), "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sender.Contents.GetModDirectory(disabled.Id), file);
            Assert.Equal(await File.ReadAllBytesAsync(file), await File.ReadAllBytesAsync(Path.Combine(receiver.Contents.GetModDirectory(importedDisabled.Id), relative)));
        }
        Assert.DoesNotContain(Directory.GetFiles(receiver.Game), file => file.Contains(".patch_"));
        var another = await new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination);
        Assert.NotEqual(imported.Id, another.Id); Assert.Equal(2, (await receiver.Library.LoadAsync()).Mods.Count);
    }

    [Fact]
    public async Task EmptyProfilesRoundTripWithoutMods()
    {
        using var sender = new Fixture(); using var receiver = new Fixture();
        var profile = ProfileEditor.AddGroup(ProfileEditor.Create("Empty"), "Future mods");
        await sender.Library.SaveProfileAsync(profile);
        var destination = Path.Combine(sender.Root, "empty.zip");
        await new ProfileArchives(sender.Store, sender.Contents).ExportAsync(profile.Id, destination);
        var imported = await new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination);
        Assert.Equal("Empty", imported.Name); Assert.Empty(imported.Entries); Assert.Equal("Future mods", Assert.Single(imported.Groups).Name);
        Assert.Empty((await receiver.Library.LoadAsync()).Mods);
    }

    [Fact]
    public async Task ChangedModContentRejectsImportAndRemovesPreviouslyImportedFiles()
    {
        using var sender = new Fixture(); using var receiver = new Fixture();
        var a = await sender.Library.ImportAsync(sender.Source("A", 1)); var b = await sender.Library.ImportAsync(sender.Source("B", 2));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Loadout"), a), b);
        await sender.Library.SaveProfileAsync(profile);
        var destination = Path.Combine(sender.Root, "bad.zip");
        await new ProfileArchives(sender.Store, sender.Contents).ExportAsync(profile.Id, destination);
        using (var zip = ZipFile.Open(destination, ZipArchiveMode.Update))
        {
            var name = $"mods/{b.Id:N}/{Fixture.Archive}.patch_7.stream";
            zip.GetEntry(name)!.Delete();
            using var output = zip.CreateEntry(name).Open(); output.Write([7, 8, 9]);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination));
        var state = await receiver.Library.LoadAsync(); Assert.Empty(state.Mods); Assert.Empty(state.Profiles);
        Assert.Empty(Directory.GetDirectories(Path.Combine(receiver.App, "library")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("PROFILE.JSON")]
    [InlineData("unexpected.txt")]
    public async Task UnsafeDuplicateOrUnexpectedEntriesAreRejected(string name)
    {
        using var sender = new Fixture(); using var receiver = new Fixture();
        var profile = ProfileEditor.Create("Empty"); await sender.Library.SaveProfileAsync(profile);
        var destination = Path.Combine(sender.Root, "unsafe.zip");
        await new ProfileArchives(sender.Store, sender.Contents).ExportAsync(profile.Id, destination);
        using (var zip = ZipFile.Open(destination, ZipArchiveMode.Update))
        { using var output = zip.CreateEntry(name).Open(); output.Write([1]); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination));
        Assert.Empty((await receiver.Library.LoadAsync()).Profiles);
    }

    [Fact]
    public async Task ImportLimitsAndUnsupportedVersionAreRejected()
    {
        using var sender = new Fixture(); using var receiver = new Fixture();
        var mod = await sender.Library.ImportAsync(sender.Source("Mod"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Loadout"), mod); await sender.Library.SaveProfileAsync(profile);
        var destination = Path.Combine(sender.Root, "limited.zip");
        await new ProfileArchives(sender.Store, sender.Contents).ExportAsync(profile.Id, destination);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProfileArchives(receiver.Store, receiver.Contents, new(MaxFiles: 1)).ImportAsync(destination));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProfileArchives(receiver.Store, receiver.Contents, new(MaxBytes: 1)).ImportAsync(destination));
        using (var zip = ZipFile.Open(destination, ZipArchiveMode.Update))
        {
            zip.GetEntry("profile.json")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("profile.json").Open()); writer.Write("{\"version\":99}");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProfileArchives(receiver.Store, receiver.Contents).ImportAsync(destination));
        Assert.Empty((await receiver.Library.LoadAsync()).Mods);
    }

    [Fact]
    public async Task ExportDetectsChangedOriginalsAndPreservesExistingDestination()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("Mod"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Loadout"), mod); await f.Library.SaveProfileAsync(profile);
        var destination = Path.Combine(f.Root, "existing.zip"); await File.WriteAllBytesAsync(destination, [1, 2, 3]);
        await File.WriteAllBytesAsync(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files[0]), [0]);
        await Assert.ThrowsAsync<IOException>(() => new ProfileArchives(f.Store, f.Contents).ExportAsync(profile.Id, destination));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.GetFiles(f.Root, "*.tmp"));
        await Assert.ThrowsAsync<ArgumentException>(() => new ProfileArchives(f.Store, f.Contents).ExportAsync(profile.Id, Path.Combine(f.App, "profile.zip")));
    }
}
