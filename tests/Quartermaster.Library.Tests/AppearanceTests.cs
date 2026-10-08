using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Xunit;

namespace Quartermaster.Library.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public async Task TagsPersistAndFollowAnUpdatedMod()
    {
        using var f = new Fixture();
        var original = await f.Library.ImportAsync(f.Source("Original", 1));
        var replacement = await f.Library.ImportAsync(f.Source("Replacement", 2));
        await f.Library.SetTagsAsync(original.Id, [" Weapons ", "weapons", "HUD", ""]);
        await f.Library.SetTagsAsync(replacement.Id, ["Updated"]);
        await f.Library.ReplaceInProfilesAsync(original.Id, replacement.Id);
        var mod = (await f.Library.LoadAsync()).Mods.Single(mod => mod.Id == replacement.Id);
        Assert.Equal(new[] { "Weapons", "HUD", "Updated" }, mod.Tags);
        Assert.True(ModTags.Matches(mod, "weapons"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Library.SetTagsAsync(mod.Id, [new string('x', 65)]));
        Assert.Equal(mod.Tags, (await f.Library.LoadAsync()).Mods.Single(m => m.Id == mod.Id).Tags);
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#xyz123")]
    [InlineData("#12345678")]
    public void InvalidGroupColorsAreRejected(string color)
    {
        var profile = ProfileEditor.AddGroup(ProfileEditor.Create("Test"), "Group");
        Assert.Throws<ArgumentException>(() => ProfileEditor.SetGroupColors(profile, profile.Groups[0].Id, color, null));
    }

    [Fact]
    public void ProfileDefaultsAndDuplicationPreserveAppearance()
    {
        var profile = ProfileEditor.AddGroup(ProfileEditor.Create("Test"), "Group");
        Assert.Null(profile.Thumbnail); Assert.Null(profile.Groups[0].BackgroundColor);
        profile = ProfileEditor.SetGroupColors(profile, profile.Groups[0].Id, "#ffee00", "#000000") with
        { Thumbnail = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==" };
        var duplicate = ProfileEditor.Duplicate(profile);
        Assert.Equal(profile.Thumbnail, duplicate.Thumbnail);
        Assert.Equal(profile.Groups[0].BackgroundColor, duplicate.Groups[0].BackgroundColor);
        Assert.NotEqual(profile.Groups[0].Id, duplicate.Groups[0].Id);
        Assert.Throws<ArgumentException>(() => ProfileAppearance.ValidateThumbnail("not base64"));
        Assert.Throws<ArgumentException>(() => ProfileAppearance.ValidateThumbnail(Convert.ToBase64String([1, 2, 3])));
    }
}
