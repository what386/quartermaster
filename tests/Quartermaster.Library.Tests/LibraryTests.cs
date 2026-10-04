using Quartermaster.Core.Patching;
using global::System.IO.Compression;
using global::System.Text.Json;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Storage;
using Xunit;

namespace Quartermaster.Library.Tests;

public class LibraryTests
{
    [Fact]
    public async Task ImportsCleanCentralTemporaryStorageOnSuccessAndFailure()
    {
        using var f = new Fixture();
        var mod = await f.Library.ImportAsync(f.Source("valid"));
        Assert.True(Directory.Exists(f.Contents.GetModDirectory(mod.Id)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.App, "temp")));
        var invalid = Path.Combine(f.Root, "invalid.zip"); await File.WriteAllBytesAsync(invalid, [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(invalid));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.App, "temp")));
        Assert.Single(Directory.GetDirectories(Path.Combine(f.App, "library")));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Library.ImportAsync(Path.Combine(f.App, "temp")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.App, "temp")));
    }

    [Fact]
    public async Task ImportsRejectRedirectedTemporaryStorage()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var source = f.Source("valid");
        Directory.CreateDirectory(f.App);
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(f.App, "temp"), outside);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(f.App, "library")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionArtworkResolvesNestedManifestPathsForExistingImports(bool zip)
    {
        using var f = new Fixture(); var source = f.Source("nested");
        Directory.CreateDirectory(Path.Combine(source, "images"));
        File.WriteAllBytes(Path.Combine(source, "images", "default.png"), [1]);
        File.WriteAllBytes(Path.Combine(source, "images", "choice.png"), [2]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """
            {"Version":1,"Options":[{"Name":"Style","Image":"images/default.png","SubOptions":[
              {"Name":"Preview","Description":"Choice description","Image":"images/choice.png","Include":[""]},
              {"Name":"Unsafe","Image":"../outside.png","Include":[""]},
              {"Name":"Missing","Image":"images/missing.png","Include":[""]}]}]}
            """);
        var wrapper = Path.Combine(f.Root, "wrapper"); Directory.CreateDirectory(wrapper);
        Directory.Move(source, Path.Combine(wrapper, "nested"));
        var mod = await f.Library.ImportAsync(zip ? f.Zip(wrapper) : wrapper);
        mod = Assert.Single((await f.Library.LoadAsync()).Mods);
        var images = Assert.Single(f.Contents.GetOptionImages(mod));
        Assert.Equal(Assert.Single(mod.Options).Id, images.OptionId);
        Assert.Equal(Path.Combine(f.Contents.GetModDirectory(mod.Id), "nested", "images", "default.png"), images.ImagePath);
        Assert.Equal(Path.Combine(f.Contents.GetModDirectory(mod.Id), "nested", "images", "choice.png"), images.Choices[0].ImagePath);
        Assert.Equal("Choice description", images.Choices[0].Description);
        Assert.Null(images.Choices[1].ImagePath); Assert.Null(images.Choices[2].ImagePath);
    }

    [Theory]
    [InlineData("icon.png", true)]
    [InlineData("../outside.png", false)]
    [InlineData("missing.png", false)]
    public async Task ManifestIconIsResolvedRelativeToManifestAndConfinedToMod(string icon, bool valid)
    {
        using var f = new Fixture(); var source = f.Source("nested");
        File.WriteAllBytes(Path.Combine(source, "icon.png"), [1]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), global::System.Text.Json.JsonSerializer.Serialize(new { Version = 1, IconPath = icon }));
        var wrapper = Path.Combine(f.Root, "wrapper"); Directory.CreateDirectory(wrapper);
        Directory.Move(source, Path.Combine(wrapper, "nested"));
        var mod = await f.Library.ImportAsync(f.Zip(wrapper));
        var reloaded = Assert.Single((await f.Library.LoadAsync()).Mods);
        Assert.Equal(valid ? Path.Combine(f.Contents.GetModDirectory(mod.Id), "nested", "icon.png") : null, f.Contents.GetIconPath(reloaded));
    }

    [Fact]
    public async Task AddingLibraryModToProfilePreservesOtherProfilesAndIgnoresDuplicates()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("mod"));
        var active = ProfileEditor.Create("Active"); var target = ProfileEditor.Create("Target");
        await f.Library.SaveProfileAsync(active, makeActive: true); await f.Library.SaveProfileAsync(target);
        await f.Library.AddToProfileAsync(mod.Id, target.Id);
        await f.Library.AddToProfileAsync(mod.Id, target.Id);
        var state = await f.Library.LoadAsync(); Assert.Single(state.Mods);
        Assert.Equal(active.Id, state.ActiveProfileId); Assert.Empty(state.Profiles.Single(p => p.Id == active.Id).Entries);
        Assert.Equal(mod.Id, Assert.Single(state.Profiles.Single(p => p.Id == target.Id).Entries).ModId);
    }

    [Fact]
    public async Task ReimportReusesStoredModAcrossFoldersAndZipsAndPreservesProfileSelections()
    {
        using var f = new Fixture(); var source = f.Source("Original");
        var mod = await f.Library.ImportAsync(source, "Custom name");
        var target = ProfileEditor.SetEnabled(ProfileEditor.Add(ProfileEditor.Create("Target"), mod), mod.Id, false);
        await f.Library.SaveProfileAsync(target);
        var wrapper = Path.Combine(f.Root, "wrapper"); Directory.CreateDirectory(wrapper);
        Directory.Move(source, Path.Combine(wrapper, "renamed"));
        var duplicate = await f.Library.ImportAsync(f.Zip(wrapper), profileId: target.Id);
        Assert.Equal(mod.Id, duplicate.Id); Assert.Equal("Custom name", duplicate.Name);
        Assert.Single((await f.Library.LoadAsync()).Mods);
        Assert.False(Assert.Single((await f.Library.LoadAsync()).Profiles[0].Entries).Enabled);
        Assert.Single(Directory.GetDirectories(Path.Combine(f.App, "library")));
        var other = ProfileEditor.Create("Other"); await f.Library.SaveProfileAsync(other);
        await f.Library.ImportAsync(wrapper, profileId: other.Id);
        Assert.Equal(mod.Id, Assert.Single((await f.Library.LoadAsync()).Profiles.Single(p => p.Id == other.Id).Entries).ModId);
    }

    [Fact]
    public async Task DifferentPatchContentsVersionsAndOptionsAreKeptAsSeparateMods()
    {
        using var f = new Fixture(); var source = f.Source("Mod");
        var manifest = Path.Combine(source, "manifest.json");
        await File.WriteAllTextAsync(manifest, """{"Guid":"86bc5845-f890-4685-9d46-9c213ff36ba8","ModVersion":"1"}""");
        var original = await f.Library.ImportAsync(source);
        Assert.Equal(original.Id, (await f.Library.ImportAsync(source)).Id);
        await File.WriteAllTextAsync(manifest, """{"Guid":"86bc5845-f890-4685-9d46-9c213ff36ba8","ModVersion":"2"}""");
        Assert.NotEqual(original.Id, (await f.Library.ImportAsync(source)).Id);
        await File.WriteAllTextAsync(manifest, """{"Guid":"86bc5845-f890-4685-9d46-9c213ff36ba8","ModVersion":"2","Options":[{"Name":"Optional","Include":[""]}]}""");
        await f.Library.ImportAsync(source);
        File.WriteAllBytes(Path.Combine(source, Fixture.Archive + ".patch_7.stream"), [4, 5, 6]);
        await f.Library.ImportAsync(source);
        Assert.Equal(4, (await f.Library.LoadAsync()).Mods.Count);
    }

    [Fact]
    public async Task BulkProfileAddSkipsExistingModsAndBulkRemoveCleansAllProfilesAndStorage()
    {
        using var f = new Fixture();
        var a = await f.Library.ImportAsync(f.Source("A", 1)); var b = await f.Library.ImportAsync(f.Source("B", 2));
        var c = await f.Library.ImportAsync(f.Source("C", 3));
        var profile = ProfileEditor.SetEnabled(ProfileEditor.Add(ProfileEditor.Create("Target"), a), a.Id, false);
        await f.Library.SaveProfileAsync(profile);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Library.AddToProfileAsync([b.Id, Guid.NewGuid()], profile.Id));
        Assert.Single((await f.Library.LoadAsync()).Profiles[0].Entries);
        await f.Library.AddToProfileAsync([a.Id, b.Id, a.Id, c.Id], profile.Id);
        var entries = (await f.Library.LoadAsync()).Profiles[0].Entries;
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, entries.Select(entry => entry.ModId)); Assert.False(entries[0].Enabled);
        await f.Library.RecordUpdateCheckAsync(new(a.Id, "custom", DateTimeOffset.UtcNow, "2", null));
        await f.Library.RemoveAsync([a.Id, b.Id]);
        var state = await f.Library.LoadAsync(); Assert.Equal(c.Id, Assert.Single(state.Mods).Id);
        Assert.Equal(c.Id, Assert.Single(state.Profiles[0].Entries).ModId); Assert.Empty(state.UpdateChecks);
        Assert.False(Directory.Exists(f.Contents.GetModDirectory(a.Id))); Assert.False(Directory.Exists(f.Contents.GetModDirectory(b.Id)));
    }

    [Fact]
    public async Task ImportCanAddToSpecificProfileWithoutChangingActiveProfile()
    {
        using var f = new Fixture();
        var active = ProfileEditor.Create("Active"); var target = ProfileEditor.Create("Target");
        await f.Library.SaveProfileAsync(active, makeActive: true);
        await f.Library.SaveProfileAsync(target);
        var source = f.Source("Dropped");
        var mod = await f.Library.ImportAsync(source, profileId: target.Id);
        var state = await f.Library.LoadAsync();
        Assert.Equal(active.Id, state.ActiveProfileId);
        Assert.Empty(state.Profiles.Single(p => p.Id == active.Id).Entries);
        Assert.Equal(mod.Id, Assert.Single(state.Profiles.Single(p => p.Id == target.Id).Entries).ModId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Library.ImportAsync(source, profileId: Guid.NewGuid()));
        Assert.Single((await f.Library.LoadAsync()).Mods);
        Assert.Single(Directory.GetDirectories(Path.Combine(f.App, "library")));
    }

    [Fact]
    public async Task LegacyLibraryStateMigratesToSeparateMetadataFiles()
    {
        using var f = new Fixture();
        var modId = Guid.NewGuid(); var profileId = Guid.NewGuid(); var setId = Guid.NewGuid();
        Directory.CreateDirectory(f.App);
        await File.WriteAllTextAsync(Path.Combine(f.App, "state.json"), $$"""
            {"schemaVersion":1,"activeProfileId":"{{profileId}}","updateChecks":[],"mods":[
              {"id":"{{modId}}","name":"Legacy","description":"","version":null,"manifestId":null,
               "importedAt":"2026-01-01T00:00:00Z","options":[],"sources":[],"patchSets":[
                 {"id":"{{setId}}","archive":"{{Fixture.Archive}}","originalSlot":7,"folder":"",
                  "files":[{"relativePath":"{{Fixture.Archive}}.patch_7","kind":"Main","size":80,"sha256":"{{new string('a', 64)}}"}],
                  "resources":[{"id":1,"type":123}]}]}],
             "profiles":[{"id":"{{profileId}}","name":"Default","priority":"LastWins",
                          "entries":[{"modId":"{{modId}}","enabled":true,"options":[]}]}]}
            """);
        var state = await f.Library.LoadAsync();
        Assert.Equal(profileId, state.ActiveProfileId);
        Assert.Equal(modId, Assert.Single(state.Mods).Id);
        var selected = Assert.Single(ProfilePatches.Resolve(state, profileId).Patches);
        Assert.Equal(modId, selected.SourceId); Assert.Equal(setId, selected.PatchSetId);
        await f.Store.SaveAsync(state);
        Assert.Equal(profileId, (await new JsonLibraryStore(f.App).LoadAsync()).ActiveProfileId);
        Assert.False(File.Exists(Path.Combine(f.App, "state.json")));
        using var library = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.App, "library.json")));
        using var profiles = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(f.App, "profiles.json")));
        Assert.True(library.RootElement.TryGetProperty("mods", out _));
        Assert.False(library.RootElement.TryGetProperty("profiles", out _));
        Assert.True(profiles.RootElement.TryGetProperty("profiles", out _));
        Assert.False(profiles.RootElement.TryGetProperty("mods", out _));
        Assert.False(File.Exists(Path.Combine(f.App, "patches.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportsFolderOrZipPreservesOriginalsAndPersistsProfiles(bool zip)
    {
        using var f = new Fixture(); var source = f.Source("source");
        var before = File.ReadAllBytes(Path.Combine(source, Fixture.Archive + ".patch_7"));
        var mod = await f.Library.ImportAsync(zip ? f.Zip(source) : source, "Named mod");
        var set = Assert.Single(mod.PatchSets);
        Assert.Equal(2, set.Files.Count); Assert.Equal(7, set.OriginalSlot);
        Assert.Equal(before, File.ReadAllBytes(f.Contents.GetFilePath(mod.Id, set.Files[0])));
        File.WriteAllBytes(Path.Combine(source, Fixture.Archive + ".patch_7"), [0]);
        Assert.Equal(before, File.ReadAllBytes(f.Contents.GetFilePath(mod.Id, set.Files[0])));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Default"), mod);
        await f.Library.SaveProfileAsync(profile, makeActive: true);
        var state = await new JsonLibraryStore(f.App).LoadAsync();
        Assert.Equal(profile.Id, state.ActiveProfileId); Assert.Equal(mod.Id, Assert.Single(state.Mods).Id);
        Assert.Equal(1UL, Assert.Single(state.Mods[0].PatchSets[0].Resources).Id);
        await f.Library.SetSourcesAsync(mod.Id, [new("custom", "remote-1")]);
        await f.Library.RecordUpdateCheckAsync(new(mod.Id, "custom", DateTimeOffset.UtcNow, "v2", "file2"));
        Assert.Single((await f.Library.LoadAsync()).UpdateChecks);
        await f.Library.RemoveAsync(mod.Id);
        state = await f.Library.LoadAsync(); Assert.Empty(state.Mods); Assert.Empty(state.Profiles[0].Entries); Assert.Empty(state.UpdateChecks);
        Assert.False(Directory.Exists(f.Contents.GetModDirectory(mod.Id)));
    }

    [Fact]
    public async Task ManifestOptionsResolveFoldersAndEmptyChoices()
    {
        using var f = new Fixture();
        var root = Path.Combine(f.Root, "variants");
        foreach (var folder in new[] { "common", "blue", "red" })
        {
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllBytes(Path.Combine(root, folder, Fixture.Archive + ".patch_0"), Fixture.Patch());
        }
        await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), """
            {"version":1,"name":"Variants","guid":"86bc5845-f890-4685-9d46-9c213ff36ba8",
             "options":[{"name":"Color","include":["common"],"subOptions":[
               {"name":"Blue","include":["blue"]},{"name":"Red","include":["red"]},{"name":"Nothing","include":[]}]}]}
            """);
        var mod = await f.Library.ImportAsync(f.Zip(root));
        Assert.Equal("Variants", mod.Name); Assert.NotNull(mod.ManifestId);
        var entry = new ProfileEntry(mod.Id, true, [new(mod.Options[0].Id, true, 1)]);
        Assert.Equal(new[] { "common", "red" }, PatchSelection.Select(mod, entry).Select(s => s.Folder));
        Assert.Equal("common", Assert.Single(PatchSelection.Select(mod, entry with { Options = [new(mod.Options[0].Id, true, 2)] })).Folder);
    }

    [Theory]
    [InlineData("../escaped")]
    [InlineData("/absolute")]
    [InlineData("C:/escaped")]
    public async Task ZipTraversalIsRejectedWithoutSavingState(string name)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "bad.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(path));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.App, "library")));
    }

    [Fact]
    public async Task DuplicateCaseInsensitiveZipFilesAndImportLimitsAreRejected()
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "duplicate.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "file", "FILE" }) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("1"); }
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(path));
        var small = new ModContentStore(f.App, new(MaxBytes: 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => small.ImportAsync(f.Source("large")));
    }

    [Fact]
    public async Task CorruptedPatchesOrUnsafeManifestIncludesLeaveNoImportedContent()
    {
        using var f = new Fixture(); var source = f.Source("source");
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), """{"Version":1,"Options":[{"Name":"Unsafe","Include":["../outside"]}]}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(source));
        File.Delete(Path.Combine(source, "manifest.json"));
        File.WriteAllBytes(Path.Combine(source, Fixture.Archive + ".patch_7"), [1, 2]);
        await Assert.ThrowsAnyAsync<IOException>(() => f.Library.ImportAsync(source));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty(Directory.GetDirectories(Path.Combine(f.App, "library")));
    }

    [Fact]
    public async Task LibraryLockRejectsConcurrentWritersAndMalformedState()
    {
        using var f = new Fixture();
        await using (var lease = await f.Store.AcquireLockAsync())
            await Assert.ThrowsAsync<IOException>(async () => { await using var other = await new JsonLibraryStore(f.App).AcquireLockAsync(); });
        await File.WriteAllTextAsync(Path.Combine(f.App, "state.json"), """{"schemaVersion":99,"mods":[],"profiles":[],"activeProfileId":null}""");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Store.LoadAsync());
    }

    [Fact]
    public async Task SymbolicLinkedContentIsRejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating symlinks requires developer mode/privileges on Windows.
        using var f = new Fixture(); var source = f.Source("source");
        File.CreateSymbolicLink(Path.Combine(source, "linked"), Path.Combine(source, Fixture.Archive + ".patch_7"));
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(source));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingOrTruncatedReferencedCompanionsAreRejected(bool missing)
    {
        using var f = new Fixture(); var source = f.Source("source");
        var path = Path.Combine(source, Fixture.Archive + ".patch_7");
        var patch = File.ReadAllBytes(path);
        global::System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(patch.AsSpan(164), 20);
        File.WriteAllBytes(path, patch);
        if (missing) File.Delete(path + ".stream");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Library.ImportAsync(source));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
    }
}
