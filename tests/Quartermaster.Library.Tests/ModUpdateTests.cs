using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Library.Tests;

public sealed class ModUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementPreservesKnownDependenciesUnlessTheUpdateHasFreshMetadata(bool refreshed)
    {
        using var f = new Fixture();
        var old = await f.Library.ImportAsync(f.Source("old", 1));
        var updated = await f.Library.ImportAsync(f.Source("new", 2));
        await f.Library.SetDependenciesAsync(old.Id, [new("Requirement", "https://www.nexusmods.com/helldivers2/mods/100")]);
        if (refreshed) await f.Library.SetDependenciesAsync(updated.Id, []);
        await f.Library.ReplaceInProfilesAsync(old.Id, updated.Id);
        var replacement = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Id == updated.Id);
        Assert.True(replacement.DependenciesKnown);
        if (refreshed) Assert.Empty(replacement.Dependencies);
        else Assert.Equal("Requirement", Assert.Single(replacement.Dependencies).Name);
    }

    [Fact]
    public async Task ReplacementPreservesProfilePositionGroupsDisabledStateAndDoesNotDuplicate()
    {
        using var f = new Fixture(); var old = await f.Library.ImportAsync(f.Source("old", 1));
        var other = await f.Library.ImportAsync(f.Source("other", 2)); var updated = await f.Library.ImportAsync(f.Source("new", 3));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Profile"), old), other);
        profile = ProfileEditor.AddGroup(profile, "Group", [old.Id, other.Id]);
        profile = ProfileEditor.SetEnabled(profile, old.Id, false);
        profile = ProfileEditor.Add(profile, updated);
        await f.Library.SaveProfileAsync(profile, true);
        await f.Library.ReplaceInProfilesAsync(old.Id, updated.Id);
        var state = await f.Library.LoadAsync(); var result = Assert.Single(state.Profiles);
        Assert.Equal(3, state.Mods.Count); Assert.Equal(profile.Id, state.ActiveProfileId);
        Assert.Equal(new[] { updated.Id, other.Id }, result.Entries.Select(e => e.ModId));
        Assert.False(result.Entries[0].Enabled); Assert.Equal(profile.Groups[0].Id, result.Entries[0].GroupId);
        Assert.Equal(profile.Groups, result.Groups);
    }
    [Fact]
    public async Task ReplacementMatchesOptionAndChoiceNamesInsteadOfGeneratedIdsOrChoiceIndex()
    {
        using var f = new Fixture(); var oldSource = f.Source("old", 1); var newSource = f.Source("new", 2);
        await File.WriteAllTextAsync(Path.Combine(oldSource, "manifest.json"), """
            {"Version":1,"Options":[{"Name":"Style","SubOptions":[{"Name":"A","Include":[""]},{"Name":"B","Include":[""]}]}]}
            """);
        await File.WriteAllTextAsync(Path.Combine(newSource, "manifest.json"), """
            {"Version":1,"Options":[{"Name":"Style","SubOptions":[{"Name":"B","Include":[""]},{"Name":"A","Include":[""]}]}]}
            """);
        var old = await f.Library.ImportAsync(oldSource); var updated = await f.Library.ImportAsync(newSource);
        var profile = ProfileEditor.SetOptions(ProfileEditor.Add(ProfileEditor.Create("Profile"), old), old, [new(old.Options[0].Id, true, 1)]);
        await f.Library.SaveProfileAsync(profile);
        await f.Library.ReplaceInProfilesAsync(old.Id, updated.Id);
        var selection = Assert.Single(Assert.Single(Assert.Single((await f.Library.LoadAsync()).Profiles).Entries).Options);
        Assert.Equal(updated.Options[0].Id, selection.OptionId); Assert.Equal(0, selection.ChoiceIndex);
    }
    [Fact]
    public async Task IncompatibleOptionsLeaveAllProfilesUnchanged()
    {
        using var f = new Fixture(); var oldSource = f.Source("old", 1);
        await File.WriteAllTextAsync(Path.Combine(oldSource, "manifest.json"), """
            {"Version":1,"Options":[{"Name":"Style","Include":[""]}]}
            """);
        var old = await f.Library.ImportAsync(oldSource); var updated = await f.Library.ImportAsync(f.Source("new", 2));
        var profile = ProfileEditor.SetOptions(ProfileEditor.Add(ProfileEditor.Create("Profile"), old), old, [new(old.Options[0].Id)]);
        await f.Library.SaveProfileAsync(profile);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Library.ReplaceInProfilesAsync(old.Id, updated.Id));
        var state = await f.Library.LoadAsync(); Assert.Equal(old.Id, Assert.Single(Assert.Single(state.Profiles).Entries).ModId);
        Assert.Equal(2, state.Mods.Count);
    }
}
