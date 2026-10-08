using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Interop.Arsenal;

public sealed record ArsenalChoiceSelection(string Name, bool Enabled);
public sealed record ArsenalOptionSelection(string Name, bool Enabled, IReadOnlyList<ArsenalChoiceSelection> Choices);
public sealed record ArsenalMod(string Id, string Name, string Directory, string Description,
    DateTimeOffset? AddedAt, SourceReference? Source)
{
    public IReadOnlyList<string> Tags { get; init; } = [];
}
public sealed record ArsenalRow(string Id, string? GroupName, bool Enabled, IReadOnlyList<ArsenalOptionSelection>? Options)
{
    public string? BackgroundColor { get; init; }
    public string? TextColor { get; init; }
}
public sealed record ArsenalProfile(string Key, string Name, PriorityDirection Priority, IReadOnlyList<ArsenalRow> Rows)
{
    public string? Thumbnail { get; init; }
}
public sealed record ArsenalImportPlan(string Directory, string? SelectedProfile,
    IReadOnlyList<ArsenalMod> Mods, IReadOnlyList<ArsenalProfile> Profiles)
{
    public int GroupCount => Profiles.Sum(profile => profile.Rows.Count(row => row.GroupName is not null));
}
public sealed record ArsenalImportProgress(int Current, int Total, string Name);
public sealed record ArsenalImportResult(IReadOnlyList<Profile> Profiles, int AddedMods, int ReusedMods, int AddedProfiles, Guid ActiveProfileId);
