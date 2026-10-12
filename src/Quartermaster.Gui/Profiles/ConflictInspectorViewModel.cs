using Quartermaster.Core.Deployment;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Profiles;

public sealed class ConflictResourceRow(ResourceConflict conflict, IReadOnlyDictionary<Guid, string> names) : ViewModelBase
{
    public ResourceConflict Conflict { get; } = conflict;
    public string Archive => Conflict.Archive;
    public string Resource => $"{Conflict.Resource.Id:x16}/{Conflict.Resource.Type:x16}";
    public string Contributors => string.Join(" · ", Conflict.SourceIds.Select(id => names[id]));
    public string Winner => Localizer.Interpolate($"{names[Conflict.WinningSourceId]} wins");
    public bool Matches(string query) => (Archive + " " + Resource + " " + Contributors).Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed class ConflictContributorRow(string name, string? version, string description, int position, bool wins, string? iconPath, IReadOnlyList<string> files) : ViewModelBase
{
    public string Name => name;
    public string Version => version ?? Localizer.Text("Unknown version");
    public string Description => description;
    public string Order => Localizer.Interpolate($"Loadout position {position}");
    public bool IsWinner => wins;
    public string Outcome => Localizer.Text(wins ? "Wins this resource" : "Overridden for this resource");
    public string? IconPath => iconPath;
    public string Files => string.Join("\n", files);
}

public sealed class ConflictInspectorViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices services;
    private readonly Guid profileId;
    private string search = "";
    private int priorityChoice;
    private ConflictResourceRow? selected;
    public string ProfileName => services.Session.State.Profiles.FirstOrDefault(profile => profile.Id == profileId)?.Name ?? "";
    public string Title => Localizer.Interpolate($"Conflict inspector: {ProfileName}");
    public IReadOnlyList<string> PriorityChoices => Localizer.Current.Choices("Later entries win", "Earlier entries win");
    public int PriorityChoice { get => priorityChoice; set { if (value is >= 0 and <= 1 && Set(ref priorityChoice, value)) Refresh(); } }
    public bool IsPreview => services.Session.State.Profiles.FirstOrDefault(profile => profile.Id == profileId) is { } profile && (int)profile.Priority != PriorityChoice;
    public string PriorityExplanation => Localizer.Text(IsPreview
        ? "Preview only. The profile and deployed files have not changed."
        : "Showing the profile's configured priority. Only enabled mods and selected options are included.");
    public IReadOnlyList<ConflictResourceRow> Resources { get; private set; } = [];
    public IReadOnlyList<ConflictResourceRow> VisibleResources => Resources.Where(row => row.Matches(Search.Trim())).ToArray();
    public string Search { get => search; set { if (Set(ref search, value)) { Notify(nameof(VisibleResources)); Notify(nameof(HasVisibleResources)); SelectVisibleResource(); } } }
    public bool HasVisibleResources => VisibleResources.Count > 0;
    public string Summary => Mods.ModPresentation.Count(Resources.Count, "overlapping resource") + " · " + Mods.ModPresentation.Count(SharedArchives.Count, "shared archive");
    public IReadOnlyList<string> SharedArchives { get; private set; } = [];
    public ConflictResourceRow? SelectedResource
    {
        get => selected;
        set { if (Set(ref selected, value)) RefreshContributors(); }
    }
    public bool HasSelectedResource => SelectedResource is not null;
    public IReadOnlyList<ConflictContributorRow> Contributors { get; private set; } = [];
    public ConflictInspectorViewModel(AppServices services, Guid profileId, string? search = null)
    {
        this.services = services; this.profileId = profileId; this.search = search ?? "";
        priorityChoice = (int)services.Session.State.Profiles.Single(profile => profile.Id == profileId).Priority;
        services.Session.Changed += OnSessionChanged;
        Refresh();
    }
    private void OnSessionChanged(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        var previous = SelectedResource?.Conflict;
        var profile = services.Session.State.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        var names = services.Session.State.Mods.ToDictionary(mod => mod.Id, mod => mod.Name);
        var report = profile is null ? new ConflictReport([], []) :
            ConflictAnalyzer.Analyze(ProfilePatches.Resolve(services.Session.State, profile with { Priority = (PriorityDirection)PriorityChoice }));
        Resources = report.Resources.OrderBy(row => row.Archive, StringComparer.Ordinal).ThenBy(row => row.Resource.Type).ThenBy(row => row.Resource.Id)
            .Select(row => new ConflictResourceRow(row, names)).ToArray();
        SharedArchives = report.Archives.Select(row => row.Archive + " · " + string.Join(" · ", row.SourceIds.Select(id => names[id]))).ToArray();
        foreach (var name in new[] { nameof(ProfileName), nameof(Title), nameof(Resources), nameof(VisibleResources), nameof(HasVisibleResources), nameof(SharedArchives),
            nameof(Summary), nameof(IsPreview), nameof(PriorityExplanation) }) Notify(name);
        SelectedResource = VisibleResources.FirstOrDefault(row => row.Conflict.Archive == previous?.Archive && row.Conflict.Resource == previous?.Resource)
            ?? VisibleResources.FirstOrDefault();
        RefreshContributors();
    }
    private void SelectVisibleResource()
    {
        if (SelectedResource is null || !VisibleResources.Contains(SelectedResource)) SelectedResource = VisibleResources.FirstOrDefault();
    }
    private void RefreshContributors()
    {
        var profile = services.Session.State.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        if (SelectedResource is not { Conflict: var conflict } || profile is null) Contributors = [];
        else Contributors = conflict.SourceIds.Select(id =>
        {
            var mod = services.Session.State.Mods.Single(mod => mod.Id == id);
            var position = profile.Entries.ToList().FindIndex(entry => entry.ModId == id);
            var files = PatchSelection.Select(mod, profile.Entries[position]).Where(set =>
                set.Archive.Equals(conflict.Archive, StringComparison.OrdinalIgnoreCase) && set.Resources.Contains(conflict.Resource))
                .SelectMany(set => set.Files).Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).ToArray();
            return new ConflictContributorRow(mod.Name, mod.Version, mod.Description, position + 1, id == conflict.WinningSourceId, services.Session.GetIconPath(mod), files);
        }).ToArray();
        Notify(nameof(Contributors)); Notify(nameof(HasSelectedResource));
    }
    public void Dispose() => services.Session.Changed -= OnSessionChanged;
}
