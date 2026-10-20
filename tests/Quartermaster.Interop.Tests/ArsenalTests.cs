using System.Text.Json;
using Quartermaster.Interop.Arsenal;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Interop.Tests;

public sealed class ArsenalTests
{
    private static readonly string A = Guid.NewGuid().ToString();
    private static readonly string B = Guid.NewGuid().ToString();
    private static readonly string Unused = Guid.NewGuid().ToString();

    private static async Task<string> ArsenalData(Fixture f, bool topPriority = false)
    {
        var root = Path.Combine(f.Root, "arsenal");
        var mods = Path.Combine(root, "mods"); Directory.CreateDirectory(mods);
        var images = Path.Combine(root, "profile-images"); Directory.CreateDirectory(images);
        await File.WriteAllBytesAsync(Path.Combine(images, "first.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg=="));
        var a = f.Source("a", 1); var b = f.Source("b", 2);
        var common = Path.Combine(a, "common"); Directory.CreateDirectory(common);
        File.Move(Path.Combine(a, Fixture.Archive + ".patch_7"), Path.Combine(common, Fixture.Archive + ".patch_7"));
        File.Move(Path.Combine(a, Fixture.Archive + ".patch_7.stream"), Path.Combine(common, Fixture.Archive + ".patch_7.stream"));
        foreach (var (name, resource) in new[] { ("red", 3UL), ("blue", 4UL) })
        {
            var variant = Path.Combine(a, name); Directory.CreateDirectory(variant);
            await File.WriteAllBytesAsync(Path.Combine(variant, Fixture.Archive + ".patch_0"), Fixture.Patch(resource));
        }
        await File.WriteAllTextAsync(Path.Combine(a, "manifest.json"), """
            {"Name":"Source name","Options":[{"Name":"Color","Include":["common"],"SubOptions":[{"Name":"Red","Include":["red"]},{"Name":"Blue","Include":["blue"]}]}]}
            """);
        Directory.Move(a, Path.Combine(mods, "a")); Directory.Move(b, Path.Combine(mods, "b"));
        // Old absolute Windows paths exercise portability on all test platforms.
        var data = new
        {
            setTopPriority = topPriority,
            selectedProfile = "second",
            profileOrder = new[] { "second", "first" },
            modsLibrary = new object[]
            {
                new { uuid=A, label="Renamed A", path=@"C:\Users\Old\hd2arsenal\mods\a", description="Original description",
                    addedAt="2026-07-03T22:40:18.129Z", nexusData=new { mod_id=123, file_id=456, version="1.2.3", game_domain_name="helldivers2" } },
                new { uuid=B, label="B", path=@"C:\Users\Old\hd2arsenal\mods\b", tags=new[]{"Weapons","UI"} },
                new { uuid=Unused, label="Unused missing mod", path=@"C:\Users\Old\hd2arsenal\mods\unused" }
            },
            modsList = new Dictionary<string, object>
            {
                ["first"] = new
                {
                    label = "First",
                    imagePath = @"C:\Users\Old\hd2arsenal\profile-images\first.png",
                    mods = new object[] {
                    new { uuid=B, enabled=true },
                    new { uuid="sep1", type="separator", label="Weapons", backgroundColor="#fee800",textColor="#000000" },
                    new { uuid=A, enabled=false, optionsConfig=new[]{new {name="Color",enabled=true,suboptions=new[]{new {name="Blue",enabled=true}}}} },
                    new { uuid="sep2",type="separator",label="Empty group" }
                }
                },
                ["second"] = new
                {
                    label = "Second",
                    mods = new object[] {
                    new { uuid="sep3",type="separator",label="Different group" },
                    new { uuid=A, enabled=true, optionsConfig=new[]{new {name="Color",enabled=false,suboptions=new[]{new {name="Blue",enabled=true}}}} },
                    new { uuid=B, enabled=false }
                }
                }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "hd2a_data.json"), JsonSerializer.Serialize(data));
        return root;
    }

    [Theory]
    [InlineData(false, PriorityDirection.LastWins)]
    [InlineData(true, PriorityDirection.FirstWins)]
    public async Task ImportsCopiedModVariantsProfileOrderGroupsEnableStatesAndChoices(bool top, PriorityDirection priority)
    {
        using var f = new Fixture(); var root = await ArsenalData(f, top);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "hd2a_data.json"));
        var existing = ProfileEditor.Create("Existing"); await f.Library.SaveProfileAsync(existing, true);
        var plan = await ArsenalReader.ReadAsync(root);
        Assert.Equal(new[] { "Second", "First" }, plan.Profiles.Select(profile => profile.Name));
        Assert.Equal(2, plan.Mods.Count); Assert.Equal(3, plan.GroupCount);
        var importer = new ArsenalImporter(f.Store, f.Contents);
        var result = await importer.ImportAsync(plan);
        Assert.Equal(2, result.AddedMods); Assert.Equal(2, result.AddedProfiles);
        var state = await f.Library.LoadAsync(); Assert.Equal(3, state.Profiles.Count);
        Assert.Contains(state.Profiles, profile => profile.Id == existing.Id && profile.Name == existing.Name);
        Assert.Equal(result.Profiles[0].Id, state.ActiveProfileId);
        var a = state.Mods.Single(mod => mod.Name == "Renamed A");
        Assert.Equal(3, a.PatchSets.Count); Assert.Equal("1.2.3", a.Version);
        Assert.Equal("123", Assert.Single(a.Sources).ModId); Assert.Equal("456", a.Sources[0].FileId);
        Assert.Equal("2026-07-03T22:40:18.1290000+00:00", a.ImportedAt.ToString("O"));
        var first = result.Profiles.Single(profile => profile.Name == "First");
        Assert.Equal("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==", first.Thumbnail);
        Assert.Equal("#FEE800", first.Groups[0].BackgroundColor); Assert.Equal("#000000", first.Groups[0].TextColor);
        Assert.Equal(new[] { "Weapons", "UI" }, state.Mods.Single(mod => mod.Name == "B").Tags);
        Assert.Equal(priority, first.Priority);
        Assert.Equal(new[] { "Weapons", "Empty group" }, first.Groups.Select(group => group.Name));
        Assert.Null(first.Entries[0].GroupId); Assert.Equal(first.Groups[0].Id, first.Entries[1].GroupId);
        Assert.True(first.Entries[0].Enabled); Assert.False(first.Entries[1].Enabled);
        var option = Assert.Single(first.Entries[1].Options); Assert.True(option.Enabled); Assert.Equal(1, option.ChoiceIndex);
        var second = result.Profiles[0]; Assert.False(Assert.Single(second.Entries[0].Options).Enabled);
        Assert.Equal(1, Assert.Single(second.Entries[0].Options).ChoiceIndex);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "hd2a_data.json")));
        Assert.True(Directory.Exists(Path.Combine(root, "mods", "a")));
        var repeated = await importer.ImportAsync(plan);
        Assert.Equal(0, repeated.AddedMods); Assert.Equal(2, repeated.ReusedMods); Assert.Equal(0, repeated.AddedProfiles);
        Assert.Equal(3, (await f.Library.LoadAsync()).Profiles.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportsTrailingCommasInProfileDataAndModernOrLegacyManifests(bool legacy)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var statePath = Path.Combine(root, "hd2a_data.json");
        var data = (await File.ReadAllTextAsync(statePath)).Replace("[\"second\",\"first\"]", "[\"second\",\"first\",]");
        data = data[..^1] + ",}";
        await File.WriteAllTextAsync(statePath, data);
        var folder = Path.Combine(root, "mods", "b");
        var optionFolder = Path.Combine(folder, "Base"); Directory.CreateDirectory(optionFolder);
        foreach (var file in Directory.EnumerateFiles(folder)) File.Move(file, Path.Combine(optionFolder, Path.GetFileName(file)));
        var manifest = legacy
            ? """{"Version":"1.03","Options":["Base",],}"""
            : """{"Version":1,"Options":[{"Name":"Base","Include":["Base",],},],}""";
        var manifestPath = Path.Combine(folder, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        Assert.Equal(new[] { "Second", "First" }, plan.Profiles.Select(profile => profile.Name));
        var result = await new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan);
        Assert.Equal(2, result.AddedMods); Assert.Equal(2, result.AddedProfiles);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Equal("Base", Assert.Single(mod.Options).Name);
        Assert.Equal(data, await File.ReadAllTextAsync(statePath));
        Assert.Equal(manifest, await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task SelectedProfilesResolveReferenceOnlyModsAndDoNotRequireUnusedLibraryFiles()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var plan = await ArsenalReader.ReadAsync(root, ["second"]);
        Assert.Single(plan.Profiles); Assert.Equal(2, plan.Mods.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArsenalReader.ReadAsync(root, ["missing"]));
        Directory.Delete(Path.Combine(root, "mods", "a"), true);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => ArsenalReader.ReadAsync(root));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
    }

    [Fact]
    public async Task LegacyStringOptionsAreConvertedInTheCopyAndRemainSelectable()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var folder = Path.Combine(root, "mods", "b");
        var optionFolder = Path.Combine(folder, "Legacy option"); Directory.CreateDirectory(optionFolder);
        foreach (var file in Directory.EnumerateFiles(folder)) File.Move(file, Path.Combine(optionFolder, Path.GetFileName(file)));
        var manifest = """{"Name":"Legacy mod","Version":"1.03","Options":["Legacy option"]}""";
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        await new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Equal("Legacy option", Assert.Single(mod.Options).Name);
        Assert.Equal("1.03", mod.Version);
        Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(folder, "manifest.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManifestlessFolderOptionsPreserveProfileSelectionsAndReimport(bool wrapped)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var source = Path.Combine(root, "mods", "b");
        var name = wrapped ? "VocaloidSamplesSFX v2 Patched" : "B";
        var original = await File.ReadAllBytesAsync(Path.Combine(source, Fixture.Archive + ".patch_7"));
        if (wrapped)
        {
            var folder = Path.Combine(source, name); Directory.CreateDirectory(folder);
            foreach (var path in Directory.GetFiles(source)) File.Move(path, Path.Combine(folder, Path.GetFileName(path)));
        }
        var plan = await ArsenalReader.ReadAsync(root);
        plan = plan with
        {
            Profiles = plan.Profiles.Select(profile => profile with
            {
                Rows = profile.Rows.Select(row => row.Id == B ? row with
                { Options = [new(name, profile.Name == "First", [])] } : row).ToArray()
            }).ToArray()
        };
        var importer = new ArsenalImporter(f.Store, f.Contents);
        var result = await importer.ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        var option = Assert.Single(mod.Options);
        Assert.Equal(name, option.Name);
        Assert.Equal(mod.PatchSets.Select(set => set.Id), option.PatchSetIds);
        foreach (var profile in result.Profiles)
        {
            var entry = profile.Entries.Single(entry => entry.ModId == mod.Id);
            var enabled = profile.Name == "First";
            Assert.Equal(enabled, entry.Enabled);
            Assert.Equal(enabled, Assert.Single(entry.Options).Enabled);
            Assert.Equal(enabled ? 1 : 0, PatchSelection.Select(mod, entry with { Enabled = true }).Count);
        }
        var main = mod.PatchSets.Single().Files.Single(file => file.Kind == Quartermaster.Core.Patching.PatchFileKind.Main);
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Contents.GetFilePath(mod.Id, main)));
        Assert.Empty(Directory.GetFiles(source, "manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(f.Contents.GetModDirectory(mod.Id), "manifest.json", SearchOption.AllDirectories));
        var repeated = await importer.ImportAsync(plan);
        Assert.Equal(0, repeated.AddedMods); Assert.Equal(2, repeated.ReusedMods); Assert.Equal(0, repeated.AddedProfiles);
    }

    [Fact]
    public async Task ManifestlessUnselectedFoldersRemainDisabledInsteadOfBecomingSharedPatches()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var source = Path.Combine(root, "mods", "b");
        var red = Path.Combine(source, "Red"); var blue = Path.Combine(source, "Blue");
        Directory.CreateDirectory(red); Directory.CreateDirectory(blue);
        foreach (var path in Directory.GetFiles(source)) File.Move(path, Path.Combine(red, Path.GetFileName(path)));
        await File.WriteAllBytesAsync(Path.Combine(blue, Fixture.Archive + ".patch_0"), Fixture.Patch(30));
        var plan = await ArsenalReader.ReadAsync(root);
        plan = plan with
        {
            Profiles = plan.Profiles.Select(profile => profile with
            { Rows = profile.Rows.Select(row => row.Id == B ? row with { Options = [new("Red", true, [])] } : row).ToArray() }).ToArray()
        };
        var result = await new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Equal(2, mod.Options.Count);
        foreach (var profile in result.Profiles)
        {
            var entry = profile.Entries.Single(entry => entry.ModId == mod.Id);
            Assert.Equal("Red", Assert.Single(PatchSelection.Select(mod, entry with { Enabled = true })).Folder);
            Assert.False(entry.Options.Single(selection => selection.OptionId == mod.Options.Single(option => option.Name == "Blue").Id).Enabled);
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("manifest")]
    [InlineData("choice")]
    public async Task FolderOptionRestorationRejectsUnmatchedOrAmbiguousConfigurations(string failure)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var source = Path.Combine(root, "mods", "b");
        var name = "B";
        if (failure == "missing") name = "Missing folder";
        if (failure == "manifest") await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), "{\"Options\":[]}");
        if (failure == "ambiguous")
        {
            name = "Same";
            foreach (var parent in new[] { "First", "Second" })
            {
                var folder = Path.Combine(source, parent, name); Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, Fixture.Archive + ".patch_0"), Fixture.Patch(parent == "First" ? 11UL : 12UL));
            }
        }
        var plan = await ArsenalReader.ReadAsync(root);
        plan = plan with
        {
            Profiles = plan.Profiles.Select(profile => profile with
            { Rows = profile.Rows.Select(row => row.Id == B ? row with
                { Options = [new(name, true, failure == "choice" ? [new("Unknown choice", true)] : [])] } : row).ToArray() }).ToArray()
        };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Contains("B", error.Message);
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.App, "library")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExistingIncludesWithoutPatchesRemainNoOpOptions(bool legacy, bool empty)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var folder = Path.Combine(root, "mods", "b");
        var header = Path.Combine(folder, "Header"); Directory.CreateDirectory(header);
        if (!empty) await File.WriteAllTextAsync(Path.Combine(header, "readme.txt"), "Presentation only");
        var manifest = legacy
            ? """{"Version":"1.03","Options":["Header"]}"""
            : """{"Version":1,"Options":[{"Name":"Header","Include":["Header"]}]}""";
        var path = Path.Combine(folder, "manifest.json"); await File.WriteAllTextAsync(path, manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        plan = plan with
        {
            Profiles = plan.Profiles.Select(profile => profile with
            { Rows = profile.Rows.Select(row => row.Id == B ? row with { Options = [new("Header", true, [])] } : row).ToArray() }).ToArray()
        };
        var result = await new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Empty(Assert.Single(mod.Options).PatchSetIds);
        foreach (var profile in result.Profiles)
        {
            var entry = profile.Entries.Single(entry => entry.ModId == mod.Id);
            Assert.True(Assert.Single(entry.Options).Enabled);
            Assert.Single(PatchSelection.Select(mod, entry with { Enabled = true }));
        }
        Assert.Equal(manifest, await File.ReadAllTextAsync(path));
        if (!empty) Assert.Equal("Presentation only", await File.ReadAllTextAsync(Path.Combine(f.Contents.GetModDirectory(mod.Id), "Header", "readme.txt")));
    }

    [Fact]
    public async Task CaseMismatchedIncludesResolveInTheCopyAndPreserveChoiceSelection()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var path = Path.Combine(root, "mods", "a", "manifest.json");
        var manifest = """{"Options":[{"Name":"Color","Include":["COMMON"],"SubOptions":[{"Name":"Red","Include":["RED"]},{"Name":"Blue","Include":["BLUE"]}]}]}""";
        await File.WriteAllTextAsync(path, manifest);
        var result = await new ArsenalImporter(f.Store, f.Contents).ImportAsync(await ArsenalReader.ReadAsync(root));
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "Renamed A");
        var entry = result.Profiles.Single(profile => profile.Name == "First").Entries.Single(entry => entry.ModId == mod.Id);
        Assert.False(entry.Enabled);
        Assert.Equal(new[] { "common", "blue" }, PatchSelection.Select(mod, entry with { Enabled = true }).Select(set => set.Folder));
        Assert.Equal(1, Assert.Single(entry.Options).ChoiceIndex);
        Assert.Equal(manifest, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataIncludesInParentsAndChoicesPreserveRealPatchesAndEmptyChoices(bool wrapped)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var folder = Path.Combine(root, "mods", "a");
        if (wrapped)
        {
            var entries = Directory.GetFileSystemEntries(folder);
            var nested = Path.Combine(folder, "Pack"); Directory.CreateDirectory(nested);
            foreach (var entry in entries)
                if (Directory.Exists(entry)) Directory.Move(entry, Path.Combine(nested, Path.GetFileName(entry)));
                else File.Move(entry, Path.Combine(nested, Path.GetFileName(entry)));
            folder = nested;
        }
        var artwork = Path.Combine(folder, "Artwork"); Directory.CreateDirectory(artwork);
        await File.WriteAllTextAsync(Path.Combine(artwork, "readme.txt"), "Shared metadata");
        var manifest = """
            {"Options":[{"Name":"Color","Include":["common","Artwork"],"SubOptions":[
            {"Name":"Red","Include":["red"]},{"Name":"Blue","Include":["blue","Artwork"]},
            {"Name":"Vanilla","Include":["Artwork"]}]}]}
            """;
        var path = Path.Combine(folder, "manifest.json"); await File.WriteAllTextAsync(path, manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        plan = plan with
        {
            Profiles = plan.Profiles.Select(profile => profile.Key != "second" ? profile : profile with
            { Rows = profile.Rows.Select(row => row.Id == A ? row with { Options = [new("Color", true, [new("Vanilla", true)])] } : row).ToArray() }).ToArray()
        };
        var result = await new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "Renamed A");
        var option = Assert.Single(mod.Options);
        Assert.Equal(new[] { "Red", "Blue", "Vanilla" }, option.Choices.Select(choice => choice.Name));
        Assert.Empty(option.Choices[2].PatchSetIds);
        var first = result.Profiles.Single(profile => profile.Name == "First").Entries.Single(entry => entry.ModId == mod.Id);
        var second = result.Profiles.Single(profile => profile.Name == "Second").Entries.Single(entry => entry.ModId == mod.Id);
        var prefix = wrapped ? "Pack/" : "";
        Assert.Equal(new[] { prefix + "common", prefix + "blue" }, PatchSelection.Select(mod, first with { Enabled = true }).Select(set => set.Folder));
        Assert.Equal(prefix + "common", Assert.Single(PatchSelection.Select(mod, second)).Folder);
        Assert.Equal(2, Assert.Single(second.Options).ChoiceIndex);
        Assert.Equal(manifest, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task HeaderFolderContainingPatchesRemainsAnActualSelectableOption()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var folder = Path.Combine(root, "mods", "b"); var header = Path.Combine(folder, "Header"); Directory.CreateDirectory(header);
        foreach (var file in Directory.GetFiles(folder)) File.Move(file, Path.Combine(header, Path.GetFileName(file)));
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), """{"Options":[{"Name":"Header","Include":["Header"]}]}""");
        await new ArsenalImporter(f.Store, f.Contents).ImportAsync(await ArsenalReader.ReadAsync(root));
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Equal(mod.PatchSets.Single().Id, Assert.Single(Assert.Single(mod.Options).PatchSetIds));
    }

    [Theory]
    [InlineData("../Header")]
    [InlineData("/Header")]
    [InlineData("C:\\Header")]
    public async Task IncludeNormalizationStillRejectsUnsafePaths(string include)
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var folder = Path.Combine(root, "mods", "b");
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new
        { Options = new[] { new { Name = "Header", Include = new[] { include } } } }));
        var plan = await ArsenalReader.ReadAsync(root);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Contains("Unsafe manifest Include", error.Message);
        Assert.Empty((await f.Library.LoadAsync()).Mods);
    }

    [Fact]
    public async Task MissingIncludesIdentifyTheModAndRollBackTheImport()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var path = Path.Combine(root, "mods", "b", "manifest.json");
        var manifest = """{"Options":[{"Name":"Optional","Include":["MissingPatchFolder"]}]}""";
        await File.WriteAllTextAsync(path, manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Contains("B", error.Message);
        Assert.Contains("MissingPatchFolder", error.Message);
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
        Assert.Equal(manifest, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FailedCopyOrOptionConversionRemovesStagedModsWithoutChangingLibrary()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var plan = await ArsenalReader.ReadAsync(root);
        var rows = plan.Profiles[0].Rows.Select(row => row.Id == A ? row with
        { Options = [new("Removed option", true, [])] } : row).ToArray();
        plan = plan with { Profiles = [plan.Profiles[0] with { Rows = rows }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Empty((await f.Library.LoadAsync()).Mods); Assert.Empty((await f.Library.LoadAsync()).Profiles);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
    }

    [Fact]
    public async Task CancellingDuringCopyRollsBackNewMods()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        using var cancellation = new CancellationTokenSource();
        var plan = await ArsenalReader.ReadAsync(root);
        var progress = new CancelOnSecondMod(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArsenalImporter(f.Store, f.Contents).ImportAsync(plan, progress, cancellation.Token));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty((await f.Library.LoadAsync()).Profiles);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
    }

    private sealed class CancelOnSecondMod(CancellationTokenSource cancellation) : IProgress<ArsenalImportProgress>
    {
        public void Report(ArsenalImportProgress value) { if (value.Current == 2) cancellation.Cancel(); }
    }

    [Fact]
    public async Task FailedCommitRestoresMetadataBeforeDeletingNewContent()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var profile = ProfileEditor.Create("Existing"); await f.Library.SaveProfileAsync(profile, true);
        var plan = await ArsenalReader.ReadAsync(root);
        await Assert.ThrowsAsync<IOException>(() => new ArsenalImporter(new FailingStore(f.Store), f.Contents).ImportAsync(plan));
        var state = await f.Library.LoadAsync(); Assert.Equal(profile.Id, Assert.Single(state.Profiles).Id);
        Assert.Empty(state.Mods); Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
    }

    private sealed class FailingStore(ILibraryStore inner) : ILibraryStore
    {
        private bool fail = true;
        public ValueTask<IAsyncDisposable> AcquireLockAsync(CancellationToken ct = default) => inner.AcquireLockAsync(ct);
        public Task<LibraryState> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);
        public async Task SaveAsync(LibraryState state, CancellationToken ct = default)
        {
            await inner.SaveAsync(state, ct);
            if (fail) { fail = false; throw new IOException("Simulated commit failure"); }
        }
    }
}
