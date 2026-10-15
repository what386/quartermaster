using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;

namespace Quartermaster.Interop.Echelon;

public sealed record EchelonChoice(string Name, string Description, IReadOnlyList<int> Sets)
{
    public string? Image { get; init; }
}
public sealed record EchelonOption(string Name, string Description, IReadOnlyList<int> Sets, IReadOnlyList<EchelonChoice> Choices)
{
    public string? Image { get; init; }
}
public sealed record EchelonMod(string Id, string Name, string Directory, string Description,
    DateTimeOffset? AddedAt, string? Version, SourceReference? Source, IReadOnlyList<string> Tags,
    IReadOnlyList<IReadOnlyList<string>> Sets, IReadOnlyList<EchelonOption> Options)
{
    public string? ManifestId { get; init; }
    public string? PageLink { get; init; }
    public string? Image { get; init; }
}
public sealed record EchelonOptionSelection(bool Enabled, int ChoiceIndex);
public sealed record EchelonRow(string Id, string? GroupName, bool Enabled, IReadOnlyList<EchelonOptionSelection> Options)
{
    public string? BackgroundColor { get; init; }
    public string? TextColor { get; init; }
}
public sealed record EchelonProfile(string Key, string Name, PriorityDirection Priority, IReadOnlyList<EchelonRow> Rows)
{
    public string? Thumbnail { get; init; }
}
public sealed record EchelonImportPlan(string Directory, string? SelectedProfile,
    IReadOnlyList<EchelonMod> Mods, IReadOnlyList<EchelonProfile> Profiles)
{
    public int GroupCount => Profiles.Sum(profile => profile.Rows.Count(row => row.GroupName is not null));
}
public sealed record EchelonImportProgress(int Current, int Total, string Name);
public sealed record EchelonImportResult(IReadOnlyList<Profile> Profiles, int AddedMods, int ReusedMods, int AddedProfiles, Guid ActiveProfileId);
