using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Library.Tests;

public class ProfileTests
{
    private const string Archive = "9ba626afa44a3aa3";
    private const string Other = "0123456789abcdef";
    private static Mod Mod(params PatchSet[] sets) => new(Guid.NewGuid(), "Test", "", null, null, DateTimeOffset.UtcNow, sets, [], []);
    private static PatchSet Set(string archive = Archive, params ResourceKey[] keys) => new(Guid.NewGuid(), archive, 7, "",
        [new(archive + ".patch_7", PatchFileKind.Main, 80, new string('a', 64)), new(archive + ".patch_7.stream", PatchFileKind.Stream, 3, new string('b', 64))], keys);
    private static LibraryState State(Profile profile, params Mod[] mods) => LibraryState.Empty with { Mods = mods, Profiles = [profile], ActiveProfileId = profile.Id };

    [Fact]
    public void BulkMovesPreserveProfileOrderAndEntryStateAcrossGroups()
    {
        var a = Mod(Set()); var b = Mod(Set()); var c = Mod(Set()); var d = Mod(Set());
        var profile = ProfileEditor.Create("Selection");
        foreach (var mod in new[] { a, b, c, d }) profile = ProfileEditor.Add(profile, mod);
        profile = ProfileEditor.SetEnabled(profile, c.Id, false);
        var moved = ProfileEditor.Move(profile, [c.Id, a.Id], 4, null);
        Assert.Equal(new[] { b.Id, d.Id, a.Id, c.Id }, moved.Entries.Select(entry => entry.ModId));
        Assert.Equal(profile.Entries.Where(entry => entry.ModId == a.Id || entry.ModId == c.Id), moved.Entries.Skip(2));
        moved = ProfileEditor.Move(moved, [c.Id, a.Id], 0, null);
        Assert.Equal(new[] { a.Id, c.Id, b.Id, d.Id }, moved.Entries.Select(entry => entry.ModId));
        moved = ProfileEditor.AddGroup(moved, "Equipment");
        var grouped = ProfileEditor.Move(moved, [c.Id, a.Id], moved.Entries.Count, moved.Groups.Single().Id);
        Assert.Equal(new[] { b.Id, d.Id, a.Id, c.Id }, grouped.Entries.Select(entry => entry.ModId));
        Assert.All(grouped.Entries.Skip(2), entry => Assert.Equal(grouped.Groups.Single().Id, entry.GroupId));
        Assert.False(grouped.Entries.Last().Enabled);
        StateValidation.Validate(State(grouped, a, b, c, d));
        Assert.Throws<KeyNotFoundException>(() => ProfileEditor.Move(profile, [a.Id, Guid.NewGuid()], 0, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileEditor.Move(profile, [a.Id], 5, null));
        Assert.Same(profile, ProfileEditor.Move(profile, [], 0, null));
    }

    [Fact]
    public void MovingGroupsMovesTheirMembersAndPreservesConfiguration()
    {
        var a = Mod(Set()); var b = Mod(Set()); var c = Mod(Set());
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Test"), a), b), c);
        profile = ProfileEditor.AddGroup(profile, "First", [a.Id, b.Id]);
        profile = ProfileEditor.AddGroup(profile, "Second", [c.Id]);
        profile = ProfileEditor.SetEnabled(profile, a.Id, false);
        profile = ProfileEditor.SetGroupExpanded(profile, profile.Groups[0].Id, false);
        var moved = ProfileEditor.MoveGroup(profile, profile.Groups[0].Id, 1);
        Assert.Equal(profile.Groups.Reverse(), moved.Groups);
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, moved.Entries.Select(entry => entry.ModId));
        Assert.Equal(profile.Entries.Where(entry => entry.ModId != c.Id), moved.Entries.Skip(1));
        StateValidation.Validate(State(moved, a, b, c));
        Assert.Throws<KeyNotFoundException>(() => ProfileEditor.MoveGroup(profile, Guid.NewGuid(), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileEditor.MoveGroup(profile, profile.Groups[0].Id, 2));
    }

    [Fact]
    public void CreatingGroupFromSelectionPreservesOrderAndEntryConfiguration()
    {
        var a = Mod(Set()); var b = Mod(Set()); var c = Mod(Set());
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Test"), a), b), c);
        profile = ProfileEditor.AddGroup(profile, "Existing", [a.Id]);
        profile = ProfileEditor.SetEnabled(profile, a.Id, false);
        var originalOrder = profile.Entries.Select(entry => entry.ModId).ToArray();
        var grouped = ProfileEditor.AddGroup(profile, "Selected", [a.Id, c.Id, a.Id]);
        var groupId = grouped.Groups[1].Id;
        Assert.Equal(originalOrder.Where(id => id != b.Id), grouped.Entries.Where(entry => entry.GroupId == groupId).Select(entry => entry.ModId));
        Assert.False(grouped.Entries.Single(entry => entry.ModId == a.Id).Enabled);
        Assert.Null(grouped.Entries.Single(entry => entry.ModId == b.Id).GroupId);
        Assert.DoesNotContain(grouped.Entries, entry => entry.GroupId == grouped.Groups[0].Id);
        Assert.Throws<KeyNotFoundException>(() => ProfileEditor.AddGroup(profile, "Invalid", [a.Id, Guid.NewGuid()]));
        Assert.Single(profile.Groups);
    }

    [Fact]
    public async Task GroupsPersistMembershipAndVisibilityAndKeepDeploymentOrderAligned()
    {
        using var f = new Fixture();
        var a = await f.Library.ImportAsync(f.Source("A", 1)); var b = await f.Library.ImportAsync(f.Source("B", 2)); var c = await f.Library.ImportAsync(f.Source("C", 3));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Grouped"), a), b), c);
        profile = ProfileEditor.AddGroup(ProfileEditor.AddGroup(profile, "Weapons"), "Armor");
        var weapons = profile.Groups[0].Id; var armor = profile.Groups[1].Id;
        profile = ProfileEditor.SetGroup(profile, a.Id, weapons);
        profile = ProfileEditor.SetGroup(profile, b.Id, armor);
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, profile.Entries.Select(entry => entry.ModId));
        profile = ProfileEditor.SetEnabled(profile, a.Id, false);
        profile = ProfileEditor.SetGroupExpanded(profile, weapons, false);
        await f.Library.SaveProfileAsync(profile, makeActive: true);
        var state = await f.Library.LoadAsync(); profile = Assert.Single(state.Profiles);
        Assert.False(profile.Groups[0].IsExpanded); Assert.Equal(weapons, profile.Entries.Single(entry => entry.ModId == a.Id).GroupId);
        Assert.False(profile.Entries.Single(entry => entry.ModId == a.Id).Enabled);
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(state, profile));
        Assert.Equal(new[] { c.Id, b.Id }, plan.Patches.Select(patch => patch.SourceId));
        var renamed = ProfileEditor.RenameGroup(ProfileEditor.SetGroupExpanded(profile, weapons, true), weapons, "Renamed");
        Assert.Equal(plan.Signature, DeploymentPlanner.Create(ProfilePatches.Resolve(state, renamed)).Signature);
        var moved = ProfileEditor.Move(profile, b.Id, 0, null);
        Assert.Null(moved.Entries[0].GroupId); Assert.Equal(b.Id, moved.Entries[0].ModId);
        var removed = ProfileEditor.RemoveGroup(profile, weapons);
        Assert.Equal(3, removed.Entries.Count); Assert.Null(removed.Entries.Single(entry => entry.ModId == a.Id).GroupId);
        Assert.False(removed.Entries.Single(entry => entry.ModId == a.Id).Enabled);
        await f.Library.SaveProfileAsync(removed);
        Assert.Single(Assert.Single((await f.Library.LoadAsync()).Profiles).Groups);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Store.SaveAsync(State(profile with
        { Entries = profile.Entries.Select(entry => entry with { GroupId = Guid.NewGuid() }).ToArray() }, a, b, c)));
        Assert.Throws<KeyNotFoundException>(() => ProfileEditor.SetGroup(profile, a.Id, Guid.NewGuid()));
    }

    [Theory]
    [InlineData(PriorityDirection.LastWins)]
    [InlineData(PriorityDirection.FirstWins)]
    public void AllocatesIndependentArchiveSlotsAndHonorsPriority(PriorityDirection direction)
    {
        var a = Mod(Set(), Set(Other)); var b = Mod(Set()); var disabled = Mod(Set());
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Test"), a), disabled), b) with { Priority = direction };
        profile = ProfileEditor.SetEnabled(profile, disabled.Id, false);
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(State(profile, a, b, disabled), profile));
        var archive = plan.Patches.Where(p => p.Archive == Archive).ToArray();
        Assert.Equal(new[] { 0, 1 }, archive.Select(p => p.Slot));
        Assert.Equal(direction == PriorityDirection.LastWins ? b.Id : a.Id, archive[^1].SourceId);
        Assert.Equal(0, plan.Patches.Single(p => p.Archive == Other).Slot);
        Assert.DoesNotContain(plan.Patches, p => p.SourceId == disabled.Id);
        Assert.All(plan.Patches, p => Assert.Equal(2, p.Files.Count));
    }

    [Fact]
    public void SlotsStartAtZeroAndSignaturesTrackSelectedContent()
    {
        var a = Mod(Set(), Set()); var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), a);
        var state = State(profile, a);
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(state, profile));
        Assert.Equal(new[] { 0, 1 }, plan.Patches.Select(p => p.Slot));
        Assert.Equal(plan.Signature, DeploymentPlanner.Create(ProfilePatches.Resolve(state, profile)).Signature);
        var changed = a with { PatchSets = [a.PatchSets[0] with { Files = [a.PatchSets[0].Files[0] with { Sha256 = new string('c', 64) }] }, a.PatchSets[1]] };
        Assert.NotEqual(plan.Signature, DeploymentPlanner.Create(ProfilePatches.Resolve(State(profile, changed), profile)).Signature);
    }

    [Fact]
    public void OptionsIncludeCommonSetsAndSelectedVariantsWithoutDuplicates()
    {
        var common = Set(); var blue = Set(); var red = Set(); var extra = Set(); var unassigned = Set();
        var option = new ModOption(Guid.NewGuid(), "Color", "", [common.Id], [new("Blue", [blue.Id, common.Id]), new("Red", [red.Id])]);
        var mod = Mod(common, blue, red, extra, unassigned) with { Options = [option, new(Guid.NewGuid(), "Extra", "", [extra.Id], [])] };
        var entry = new ProfileEntry(mod.Id, true, []);
        Assert.Equal(new[] { unassigned.Id, common.Id, blue.Id, extra.Id }, PatchSelection.Select(mod, entry).Select(s => s.Id));
        entry = entry with { Options = [new(option.Id, true, 1), new(mod.Options[1].Id, false)] };
        Assert.Equal(new[] { unassigned.Id, common.Id, red.Id }, PatchSelection.Select(mod, entry).Select(s => s.Id));
        Assert.Empty(PatchSelection.Select(mod, entry with { Enabled = false }));
        Assert.Throws<ArgumentException>(() => PatchSelection.Select(mod, entry with { Options = [new(option.Id, true, 3)] }));
    }

    [Fact]
    public void ProfilesSupportEnableMoveRemoveAndRejectDuplicates()
    {
        var a = Mod(Set()); var b = Mod(Set());
        var original = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Test"), a), b);
        var moved = ProfileEditor.Move(original, b.Id, 0);
        Assert.Equal(a.Id, original.Entries[0].ModId);
        Assert.Equal(b.Id, moved.Entries[0].ModId);
        Assert.False(ProfileEditor.SetEnabled(moved, b.Id, false).Entries[0].Enabled);
        Assert.Single(ProfileEditor.Remove(moved, a.Id).Entries);
        Assert.Throws<ArgumentException>(() => ProfileEditor.Add(original, a));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileEditor.Move(original, a.Id, 4));
    }

    [Fact]
    public void ConflictReportsDistinguishArchiveOverlapFromResourceOverlap()
    {
        var shared = new ResourceKey(ulong.MaxValue, 123);
        var a = Mod(Set(Archive, shared)); var b = Mod(Set(Archive, shared)); var c = Mod(Set(Archive, new ResourceKey(1, 123)));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Test"), a), b), c);
        var report = ConflictAnalyzer.Analyze(ProfilePatches.Resolve(State(profile, a, b, c), profile));
        Assert.Equal(3, Assert.Single(report.Archives).SourceIds.Count);
        Assert.Equal(b.Id, Assert.Single(report.Resources).WinningSourceId);
        Assert.Equal(a.Id, Assert.Single(ConflictAnalyzer.Analyze(ProfilePatches.Resolve(State(profile, a, b, c), profile with { Priority = PriorityDirection.FirstWins })).Resources).WinningSourceId);
        var disabled = ProfileEditor.SetEnabled(profile, b.Id, false);
        Assert.Empty(ConflictAnalyzer.Analyze(ProfilePatches.Resolve(State(disabled, a, b, c), disabled)).Resources);
    }

    [Theory]
    [InlineData("../file")]
    [InlineData("/absolute")]
    [InlineData("C:/file")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public void RejectsUnsafeStoredPaths(string path)
    {
        var set = Set(); var mod = Mod(set with { Files = [set.Files[0] with { RelativePath = path }] });
        Assert.Throws<ArgumentException>(() => StateValidation.Validate(LibraryState.Empty with { Mods = [mod] }));
    }

    [Fact]
    public void DeploymentTrackingRecognizesMissingChangedAndDifferentSelections()
    {
        var mod = Mod(Set()); var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod);
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(State(profile, mod), profile));
        var patch = plan.Patches[0]; var file = patch.Files[0]; var name = PatchFiles.Name(Archive, 0, file.Kind);
        var ledger = new DeploymentLedger(1, "/game", profile.Id, plan.Signature, DateTimeOffset.UtcNow,
            patch.Files.Select(f => new OwnedFile(PatchFiles.Name(Archive, 0, f.Kind), Archive, 0, mod.Id, patch.PatchSetId, f.Kind, f.Size, f.Sha256, f.Sha256)).ToArray());
        var health = ledger.Files.Select(f => new TrackedFile(f.Name, ManagedFileStatus.Present)).ToArray();
        Assert.Equal(ModDeploymentStatus.Deployed, DeploymentTracking.ForMod(mod.Id, plan, ledger, health));
        Assert.Equal(ModDeploymentStatus.Damaged, DeploymentTracking.ForMod(mod.Id, plan, ledger, [new(name, ManagedFileStatus.Missing)]));
        Assert.Equal(ModDeploymentStatus.DifferentSelection, DeploymentTracking.ForMod(mod.Id, plan with { Patches = [] }, ledger, health));
        Assert.Equal(ModDeploymentStatus.NotDeployed, DeploymentTracking.ForMod(mod.Id, plan, DeploymentLedger.Empty("/game"), []));
    }
}
