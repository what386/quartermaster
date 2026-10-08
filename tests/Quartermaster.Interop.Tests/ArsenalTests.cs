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
            setTopPriority = topPriority, selectedProfile = "second", profileOrder = new[] { "second", "first" },
            modsLibrary = new object[]
            {
                new { uuid=A, label="Renamed A", path=@"C:\Users\Old\hd2arsenal\mods\a", description="Original description",
                    addedAt="2026-07-03T22:40:18.129Z", nexusData=new { mod_id=123, file_id=456, version="1.2.3", game_domain_name="helldivers2" } },
                new { uuid=B, label="B", path=@"C:\Users\Old\hd2arsenal\mods\b" },
                new { uuid=Unused, label="Unused missing mod", path=@"C:\Users\Old\hd2arsenal\mods\unused" }
            },
            modsList = new Dictionary<string,object>
            {
                ["first"] = new { label="First", mods=new object[] {
                    new { uuid=B, enabled=true },
                    new { uuid="sep1", type="separator", label="Weapons" },
                    new { uuid=A, enabled=false, optionsConfig=new[]{new {name="Color",enabled=true,suboptions=new[]{new {name="Blue",enabled=true}}}} },
                    new { uuid="sep2",type="separator",label="Empty group" }
                } },
                ["second"] = new { label="Second", mods=new object[] {
                    new { uuid="sep3",type="separator",label="Different group" },
                    new { uuid=A, enabled=true, optionsConfig=new[]{new {name="Color",enabled=false,suboptions=new[]{new {name="Blue",enabled=true}}}} },
                    new { uuid=B, enabled=false }
                } }
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
        Assert.Equal(priority, first.Priority);
        Assert.Equal(new[] { "Weapons", "Empty group" }, first.Groups.Select(group => group.Name));
        Assert.Null(first.Entries[0].GroupId); Assert.Equal(first.Groups[0].Id, first.Entries[1].GroupId);
        Assert.True(first.Entries[0].Enabled); Assert.False(first.Entries[1].Enabled);
        var option = Assert.Single(first.Entries[1].Options); Assert.True(option.Enabled); Assert.Equal(1, option.ChoiceIndex);
        var second = result.Profiles[0]; Assert.False(Assert.Single(second.Entries[0].Options).Enabled);
        Assert.Equal(1,Assert.Single(second.Entries[0].Options).ChoiceIndex);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root,"hd2a_data.json")));
        Assert.True(Directory.Exists(Path.Combine(root,"mods","a")));
        var repeated = await importer.ImportAsync(plan);
        Assert.Equal(0, repeated.AddedMods); Assert.Equal(2, repeated.ReusedMods); Assert.Equal(0, repeated.AddedProfiles);
        Assert.Equal(3, (await f.Library.LoadAsync()).Profiles.Count);
    }

    [Fact]
    public async Task SelectedProfilesResolveReferenceOnlyModsAndDoNotRequireUnusedLibraryFiles()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var plan = await ArsenalReader.ReadAsync(root, ["second"]);
        Assert.Single(plan.Profiles); Assert.Equal(2, plan.Mods.Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArsenalReader.ReadAsync(root,["missing"]));
        Directory.Delete(Path.Combine(root,"mods","a"),true);
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
        await File.WriteAllTextAsync(Path.Combine(folder,"manifest.json"),manifest);
        var plan = await ArsenalReader.ReadAsync(root);
        await new ArsenalImporter(f.Store,f.Contents).ImportAsync(plan);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Name == "B");
        Assert.Equal("Legacy option",Assert.Single(mod.Options).Name);
        Assert.Equal("1.03",mod.Version);
        Assert.Equal(manifest,await File.ReadAllTextAsync(Path.Combine(folder,"manifest.json")));
    }

    [Fact]
    public async Task FailedCopyOrOptionConversionRemovesStagedModsWithoutChangingLibrary()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var plan = await ArsenalReader.ReadAsync(root);
        var rows = plan.Profiles[0].Rows.Select(row => row.Id == A ? row with
        { Options = [new("Removed option",true,[])] } : row).ToArray();
        plan = plan with { Profiles = [plan.Profiles[0] with { Rows=rows }] };
        await Assert.ThrowsAsync<InvalidDataException>(() => new ArsenalImporter(f.Store,f.Contents).ImportAsync(plan));
        Assert.Empty((await f.Library.LoadAsync()).Mods); Assert.Empty((await f.Library.LoadAsync()).Profiles);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App,"library")));
    }

    [Fact]
    public async Task CancellingDuringCopyRollsBackNewMods()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        using var cancellation = new CancellationTokenSource();
        var plan = await ArsenalReader.ReadAsync(root);
        var progress = new CancelOnSecondMod(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ArsenalImporter(f.Store,f.Contents).ImportAsync(plan,progress,cancellation.Token));
        Assert.Empty((await f.Library.LoadAsync()).Mods);
        Assert.Empty((await f.Library.LoadAsync()).Profiles);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App,"library")));
    }

    private sealed class CancelOnSecondMod(CancellationTokenSource cancellation) : IProgress<ArsenalImportProgress>
    {
        public void Report(ArsenalImportProgress value) { if (value.Current == 2) cancellation.Cancel(); }
    }

    [Fact]
    public async Task FailedCommitRestoresMetadataBeforeDeletingNewContent()
    {
        using var f = new Fixture(); var root = await ArsenalData(f);
        var profile = ProfileEditor.Create("Existing"); await f.Library.SaveProfileAsync(profile,true);
        var plan = await ArsenalReader.ReadAsync(root);
        await Assert.ThrowsAsync<IOException>(() => new ArsenalImporter(new FailingStore(f.Store),f.Contents).ImportAsync(plan));
        var state = await f.Library.LoadAsync(); Assert.Equal(profile.Id, Assert.Single(state.Profiles).Id);
        Assert.Empty(state.Mods); Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App,"library")));
    }

    private sealed class FailingStore(ILibraryStore inner) : ILibraryStore
    {
        private bool fail = true;
        public ValueTask<IAsyncDisposable> AcquireLockAsync(CancellationToken ct=default) => inner.AcquireLockAsync(ct);
        public Task<LibraryState> LoadAsync(CancellationToken ct=default) => inner.LoadAsync(ct);
        public async Task SaveAsync(LibraryState state,CancellationToken ct=default)
        {
            await inner.SaveAsync(state,ct);
            if(fail) { fail=false; throw new IOException("Simulated commit failure"); }
        }
    }
}
