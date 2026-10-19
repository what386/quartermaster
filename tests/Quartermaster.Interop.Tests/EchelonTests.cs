using System.Text.Json;
using System.Text.Json.Nodes;
using Quartermaster.Interop.Echelon;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Interop.Tests;

public sealed class EchelonTests
{
    private static async Task<string> Data(Fixture f, bool firstWins = false)
    {
        var root = Path.Combine(f.Root, "Echelon");
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        var a = Path.Combine(root, "mods", "Options (a)"); Directory.CreateDirectory(a);
        for (var index = 0; index < 3; index++)
        {
            var part = Path.Combine(a, "opt", index.ToString()); Directory.CreateDirectory(part);
            await File.WriteAllBytesAsync(Path.Combine(part, Fixture.Archive + ".patch_0"), Fixture.Patch((ulong)index + 1));
            await File.WriteAllBytesAsync(Path.Combine(part, Fixture.Archive + ".patch_0.stream"), [(byte)index]);
        }
        Directory.Move(f.Source("b", 4), Path.Combine(root, "mods", "b"));
        Directory.Move(f.Source("unused", 5), Path.Combine(root, "mods", "unused"));
        var data = new
        {
            version = 2,
            active_profile = "second",
            settings = new { priority = firstWins ? "first" : "last" },
            library = new Dictionary<string, object>
            {
                ["a"] = new
                {
                    id = "a",
                    name = "Options",
                    store = "Options (a)",
                    description = "Description",
                    version = "1.2",
                    added_at = 1751582418.129,
                    nexus = new { mod_id = 123 },
                    tags = new[] { "Weapons" },
                    sets = Enumerable.Range(0, 3).Select(index => new
                    {
                        hash = Fixture.Archive,
                        files = new Dictionary<string, string>
                        { [""] = $"opt/{index}/{Fixture.Archive}.patch_0", [".stream"] = $"opt/{index}/{Fixture.Archive}.patch_0.stream" }
                    }).ToArray(),
                    options = new[] { new { name = "Color", description = "Pick one", include = new[] {0},
                        subs = new[] { new { name = "Red", include = new[] {1} }, new { name = "Blue", include = new[] {2} } } } },
                    option_defaults = new { on = new[] { false }, sel = new[] { 1 } }
                },
                ["b"] = new { id = "b", name = "Simple", store = @"C:\Old\Echelon\mods\b", sets = new[] { new { files = new Dictionary<string, string> { [""] = Fixture.Archive + ".patch_7", [".stream"] = Fixture.Archive + ".patch_7.stream" } } } },
                ["unused"] = new { id = "unused", name = "Unused", sets = new[] { new { files = new Dictionary<string, string> { [""] = Fixture.Archive + ".patch_7" } } } }
            },
            profiles = new object[]
            {
                new { id = "first", name = "First", entries = new object[] {
                    new {type = "mod", id = "b", enabled = false},
                    new {type = "sep", id = "g", title = "Weapons", bg = "#fee800", fg = "#000000"},
                    new {type = "mod", id = "a", enabled = true, options = new {on = new[] {true}, sel = new[] {1}}},
                    new {type = "sep", id = "empty", title = "Empty"} } },
                new { id = "second", name = "Second", entries = new object[] {new {type = "mod", id = "a", enabled = false}} }
            }
        };
        await File.WriteAllTextAsync(Path.Combine(root, "state.json"), JsonSerializer.Serialize(data));
        return root;
    }

    [Theory]
    [InlineData(false, PriorityDirection.LastWins)]
    [InlineData(true, PriorityDirection.FirstWins)]
    public async Task ImportsLibraryProfilesGroupsVariantsDefaultsAndProviderMetadataWithoutChangingSource(bool firstWins, PriorityDirection priority)
    {
        using var f = new Fixture(); var root = await Data(f, firstWins);
        var source = await File.ReadAllBytesAsync(Path.Combine(root, "state.json"));
        await f.Library.SaveProfileAsync(ProfileEditor.Create("Existing"), true);
        var plan = await EchelonReader.ReadAsync(root);
        var importer = new EchelonImporter(f.Store, f.Contents);
        var result = await importer.ImportAsync(plan);
        Assert.Equal(3, result.AddedMods); Assert.Equal(2, result.AddedProfiles);
        var state = await f.Store.LoadAsync(); Assert.Equal(3, state.Profiles.Count);
        var mod = state.Mods.Single(m => m.Name == "Options");
        Assert.Equal(3, mod.PatchSets.Count); Assert.Equal("1.2", mod.Version);
        Assert.Equal("Description", mod.Description); Assert.Equal(new[] { "Weapons" }, mod.Tags);
        Assert.Equal("123", Assert.Single(mod.Sources).ModId); Assert.Null(mod.Sources[0].FileId);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1751582418.129), mod.ImportedAt);
        var first = result.Profiles[0]; Assert.Equal(priority, first.Priority);
        Assert.Equal(new[] { "Simple", "Options" }, first.Entries.Select(e => state.Mods.Single(m => m.Id == e.ModId).Name));
        Assert.False(first.Entries[0].Enabled); Assert.True(first.Entries[1].Enabled);
        Assert.Null(first.Entries[0].GroupId); Assert.Equal(first.Groups[0].Id, first.Entries[1].GroupId);
        Assert.Equal(new[] { "Weapons", "Empty" }, first.Groups.Select(g => g.Name));
        Assert.Equal("#FEE800", first.Groups[0].BackgroundColor);
        var choice = Assert.Single(first.Entries[1].Options); Assert.True(choice.Enabled); Assert.Equal(1, choice.ChoiceIndex);
        var second = result.Profiles[1]; Assert.False(Assert.Single(second.Entries).Enabled);
        Assert.False(Assert.Single(second.Entries[0].Options).Enabled); Assert.Equal(1, second.Entries[0].Options[0].ChoiceIndex);
        Assert.Equal(second.Id, state.ActiveProfileId);
        foreach (var set in mod.PatchSets)
            foreach (var file in set.Files) Assert.True(File.Exists(f.Contents.GetFilePath(mod.Id, file)));
        Assert.Equal(source, await File.ReadAllBytesAsync(Path.Combine(root, "state.json")));
        var again = await importer.ImportAsync(plan);
        Assert.Equal(0, again.AddedMods); Assert.Equal(3, again.ReusedMods); Assert.Equal(0, again.AddedProfiles);
    }

    [Fact]
    public async Task PreservesArtworkManifestIdentityAndModPage()
    {
        using var f = new Fixture(); var root = await Data(f);
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==");
        foreach (var folder in new[] { "images", "icons", "mods/Options (a)/img" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            await File.WriteAllBytesAsync(Path.Combine(root, folder, "test.png"), image);
        }
        var path = Path.Combine(root, "state.json"); var data = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        var guid = Guid.NewGuid();
        data["library"]!["a"]!["guid"] = guid.ToString();
        data["library"]!["a"]!["web"] = "https://ayakamods.com/mods/example.123/";
        data["library"]!["a"]!["image"] = "test.png";
        data["library"]!["a"]!["options"]![0]!["image"] = "img/test.png";
        data["library"]!["a"]!["options"]![0]!["subs"]![1]!["image"] = "img/test.png";
        data["profiles"]![0]!["icon"] = "test.png";
        await File.WriteAllTextAsync(path, data.ToJsonString());
        var result = await new EchelonImporter(f.Store, f.Contents).ImportAsync(await EchelonReader.ReadAsync(root));
        var mod = (await f.Store.LoadAsync()).Mods.Single(m => m.Name == "Options");
        Assert.Equal(guid, mod.ManifestId); Assert.Equal("https://ayakamods.com/mods/example.123/", mod.PageLink);
        Assert.NotNull(result.Profiles[0].Thumbnail);
        Assert.Equal(image, await File.ReadAllBytesAsync(f.Contents.GetIconPath(mod)!));
        var artwork = Assert.Single(f.Contents.GetOptionImages(mod));
        Assert.Equal(image, await File.ReadAllBytesAsync(artwork.ImagePath!));
        Assert.Equal(image, await File.ReadAllBytesAsync(artwork.Choices[1].ImagePath!));
    }

    [Fact]
    public async Task SupportsCustomStorageAndSelectedProfiles()
    {
        using var f = new Fixture(); var root = await Data(f);
        var custom = Path.Combine(f.Root, "custom storage"); Directory.Move(Path.Combine(root, "mods"), custom);
        Directory.CreateDirectory(Path.Combine(root, "mods"));
        await File.WriteAllTextAsync(Path.Combine(root, "locations.json"), JsonSerializer.Serialize(new { mods = custom }));
        var plan = await EchelonReader.ReadAsync(root, ["second"]);
        Assert.Single(plan.Profiles); Assert.Equal(3, plan.Mods.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => EchelonReader.ReadAsync(root, ["missing"]));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("index")]
    [InlineData("unassigned")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task RejectsUnsupportedOrUnsafeState(string corruption)
    {
        using var f = new Fixture(); var root = await Data(f);
        var path = Path.Combine(root, "state.json"); var data = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        switch (corruption)
        {
            case "version": data["version"] = 99; break;
            case "path": data["library"]!["a"]!["sets"]![0]!["files"]![""] = "../outside.patch_0"; break;
            case "unassigned": data["library"]!["a"]!["options"]![0]!["include"] = new JsonArray(); break;
            case "index": data["library"]!["a"]!["options"]![0]!["include"]![0] = 99; break;
            case "missing": data["profiles"]![0]!["entries"]![0]!["id"] = "not-in-library"; break;
            case "duplicate": data["profiles"]![1]!["id"] = "first"; break;
        }
        await File.WriteAllTextAsync(path, data.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => EchelonReader.ReadAsync(root));
        Assert.Empty((await f.Store.LoadAsync()).Mods);
    }

    [Fact]
    public async Task FailedContentImportRollsBackCopiedFilesAndPreservesExistingLibrary()
    {
        using var f = new Fixture(); var root = await Data(f);
        var existing = ProfileEditor.Create("Existing"); await f.Library.SaveProfileAsync(existing, true);
        var plan = await EchelonReader.ReadAsync(root);
        File.Delete(Path.Combine(root, "mods", "b", Fixture.Archive + ".patch_7"));
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() => new EchelonImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Contains("Simple", error.Message); Assert.IsType<FileNotFoundException>(error.InnerException);
        var state = await f.Store.LoadAsync(); Assert.Empty(state.Mods); Assert.Equal(existing.Id, Assert.Single(state.Profiles).Id);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
    }

    [Fact]
    public async Task RejectsSymbolicLinksEvenWhenAddedAfterReadingPlan()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var root = await Data(f);
        var plan = await EchelonReader.ReadAsync(root);
        var target = Path.Combine(root, "mods", "Options (a)", "opt", "0");
        Directory.Move(target, target + "-original"); Directory.CreateSymbolicLink(target, target + "-original");
        await Assert.ThrowsAsync<InvalidDataException>(() => new EchelonImporter(f.Store, f.Contents).ImportAsync(plan));
        Assert.Empty((await f.Store.LoadAsync()).Mods);
    }

    [Fact]
    public async Task CancellationRemovesStagedContent()
    {
        using var f = new Fixture(); var root = await Data(f);
        var plan = await EchelonReader.ReadAsync(root);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EchelonImporter(f.Store, f.Contents).ImportAsync(plan, new Cancel(cancellation), cancellation.Token));
        Assert.Empty((await f.Store.LoadAsync()).Mods);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "library")));
    }
    private sealed class Cancel(CancellationTokenSource cancellation) : IProgress<EchelonImportProgress>
    { public void Report(EchelonImportProgress value) { if (value.Current == 2) cancellation.Cancel(); } }
}
