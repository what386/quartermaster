using Quartermaster.Library.Mods;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Shared;
using Quartermaster.Gui.Services;

namespace Quartermaster.Gui.Mods;

public sealed class ModDetailsViewModel : ViewModelBase
{
    private Mod mod;
    private readonly AppServices services;
    private readonly Guid? profileId;
    private string pageLink;
    private string savedPage;
    public string PageLink { get => pageLink; set => Set(ref pageLink, value); }
    public OperationState Operations => services.Operations;
    public AsyncCommand ResolveDependenciesCommand { get; }
    public bool CanResolveDependencies => services.Downloads.CanResolveDependencies(mod);
    public ModDetailsViewModel(Mod mod, AppServices services, Guid? profileId = null)
    {
        this.mod = mod;
        this.services = services;
        this.profileId = profileId;
        pageLink = savedPage = ModLinks.PageFor(mod) ?? "";
        RefreshRelationships();
        ResolveDependenciesCommand = services.Operations.CreateCommand("Resolving dependencies", async ct =>
        {
            await SavePageAsync(ct);
            await services.Downloads.ResolveDependenciesAsync(this.mod.Id, ct);
            RefreshRelationships();
        }, () => CanResolveDependencies);
    }
    private async Task SavePageAsync(CancellationToken ct)
    {
        if (PageLink.Trim() == savedPage) return;
        await services.Library.SetPageLinkAsync(mod.Id, PageLink, ct);
        savedPage = PageLink = ModLinks.ValidatePage(PageLink) ?? "";
        await services.Session.ReloadAsync(ct);
        RefreshRelationships();
    }
    public async Task<bool> SaveOnCloseAsync()
    {
        if (!Operations.CanInteract) return false;
        if (PageLink.Trim() == savedPage) return true;
        var saved = false;
        await Operations.RunAsync("Saving mod page", async ct =>
        {
            await SavePageAsync(ct);
            saved = true;
        }, showProgress: false);
        return saved;
    }
    public string Name => mod.Name;
    public string Description => string.IsNullOrWhiteSpace(mod.Description) ? "No description provided." : mod.Description;
    public string Version => ModPresentation.Version(mod) ?? "Not specified";
    public string ImportedAt => mod.ImportedAt.ToLocalTime().ToString("g");
    public string ResourceSummary => $"{ModPresentation.Count(mod.PatchSets.Count, "patch set")} · {ModPresentation.Count(mod.PatchSets.Sum(p => p.Resources.Count), "indexed resource")}";
    public IReadOnlyList<string> Archives => mod.PatchSets.Select(p => p.Archive).Distinct().ToArray();
    public IReadOnlyList<string> Files => mod.PatchSets.SelectMany(p => p.Files).Select(f => f.RelativePath).ToArray();
    public bool HasOptions => mod.Options.Count > 0;
    public IReadOnlyList<ModRelationshipItem> Dependencies { get; private set; } = [];
    public IReadOnlyList<ModRelationshipItem> Dependents { get; private set; } = [];
    public IReadOnlyList<string> Collisions { get; private set; } = [];
    public string CollisionSummary { get; private set; } = "";
    public bool HasCollisions => Collisions.Count > 0;
    public bool HasDependencies => Dependencies.Count > 0;
    public bool HasDependents => Dependents.Count > 0;
    public string DependencySummary => mod.DependenciesKnown ? "No dependencies." : "Dependency information unavailable.";
    internal void StartWatching() { services.Session.Changed += SessionChanged; RefreshRelationships(); }
    internal void StopWatching() => services.Session.Changed -= SessionChanged;
    private void SessionChanged(object? sender, EventArgs args) => RefreshRelationships();
    private void RefreshRelationships()
    {
        mod = services.Session.State.Mods.FirstOrDefault(item => item.Id == mod.Id) ?? mod;
        var library = services.Session.State.Mods.Where(item => !item.Superseded).ToArray();
        Dependencies = mod.Dependencies.Select(dependency => new ModRelationshipItem(
            dependency.Name,
            !dependency.CanInstall ? "External requirement" : library.Any(item => ModDependencyMatching.Matches(item, dependency))
                ? "In library" : "Not in library",
            dependency.Notes ?? "",
            dependency.Page,
            services)).ToArray();
        Dependents = library.Where(item => item.Id != mod.Id && item.Dependencies.Any(dependency => ModDependencyMatching.Matches(mod, dependency)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ModRelationshipItem(item.Name, "", "", ModLinks.PageFor(item), services)).ToArray();
        Notify(nameof(Dependencies)); Notify(nameof(Dependents));
        Notify(nameof(HasDependencies)); Notify(nameof(HasDependents)); Notify(nameof(DependencySummary));
        Notify(nameof(CanResolveDependencies)); ResolveDependenciesCommand?.Refresh();
        RefreshCollisions();
    }

    private void RefreshCollisions()
    {
        var state = services.Session.State;
        var mods = state.Mods.ToDictionary(item => item.Id);
        var collisions = state.Profiles.Where(profile => (profileId is null || profile.Id == profileId) &&
                profile.Entries.Any(entry => entry.ModId == mod.Id && entry.Enabled))
            .SelectMany(profile => ConflictAnalyzer.Analyze(ProfilePatches.Resolve(state, profile)).Resources
                .Where(conflict => conflict.SourceIds.Contains(mod.Id))
                .Select(conflict => (Profile: profile, Conflict: conflict)))
            .ToArray();
        Collisions = collisions.Select(item => $"{item.Profile.Name} · Clashes with {string.Join(", ", item.Conflict.SourceIds.Where(id => id != mod.Id).Select(id => mods[id].Name))}" +
            $" · {item.Conflict.Archive} · {item.Conflict.Resource.Id:x16}/{item.Conflict.Resource.Type:x16} · {mods[item.Conflict.WinningSourceId].Name} wins").ToArray();
        var names = collisions.SelectMany(item => item.Conflict.SourceIds).Where(id => id != mod.Id)
            .Distinct().Select(id => mods[id].Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var resources = collisions.Select(item => (item.Conflict.Archive, item.Conflict.Resource)).Distinct().Count();
        var profileCount = collisions.Select(item => item.Profile.Id).Distinct().Count();
        CollisionSummary = collisions.Length == 0 ? "" :
            $"{ModPresentation.Count(resources, "overlapping resource")} with {string.Join(", ", names.Take(3))}" +
            (names.Length > 3 ? $" and {names.Length - 3} more" : "") +
            (profileCount > 1 ? $" across {profileCount} profiles." : ".");
        Notify(nameof(Collisions)); Notify(nameof(HasCollisions)); Notify(nameof(CollisionSummary));
    }

}

public sealed class ModRelationshipItem(string name, string status, string notes, string? page, AppServices services)
{
    public string Name { get; } = name;
    public string Status { get; } = status;
    public string Notes { get; } = notes;
    public bool HasStatus => Status.Length > 0;
    public bool HasNotes => Notes.Length > 0;
    public bool HasPage { get; } = Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
    public Command OpenPageCommand { get; } = new(() => services.OpenBrowser(new Uri(page!)),
        () => Uri.TryCreate(page, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http");
}

public sealed class ModOptionsViewModel
{
    public IReadOnlyList<OptionEdit> Options { get; }
    public bool HasOptions => Options.Count > 0;
    public ModOptionsViewModel(Mod mod, IReadOnlyList<OptionSelection> selections, IReadOnlyList<ModOptionImages>? images = null)
    {
        Options = mod.Options.Select(o => new OptionEdit(o, selections.FirstOrDefault(s => s.OptionId == o.Id) ?? new(o.Id), images?.FirstOrDefault(image => image.OptionId == o.Id))).ToArray();
    }
    public IReadOnlyList<OptionSelection> Selections() => Options.Select(o => new OptionSelection(o.Id, o.Enabled, o.ChoiceIndex)).ToArray();
}

public sealed record OptionChoiceItem(string Name, string Description, string? ImagePath);

public sealed class OptionEdit : ViewModelBase
{
    private readonly string? optionImagePath;
    private bool enabled;
    private int choiceIndex;
    public Guid Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IReadOnlyList<OptionChoiceItem> Choices { get; }
    public string? PreviewImagePath => (ChoiceIndex < Choices.Count ? Choices[ChoiceIndex].ImagePath : null) ?? optionImagePath;
    public bool HasChoices => Choices.Count > 0;
    public bool Enabled { get => enabled; set => Set(ref enabled, value); }
    public int ChoiceIndex { get => choiceIndex; set { if (value >= 0 && (Choices.Count == 0 ? value == 0 : value < Choices.Count) && Set(ref choiceIndex, value)) Notify(nameof(PreviewImagePath)); } }
    public OptionEdit(ModOption option, OptionSelection selection, ModOptionImages? images = null)
    {
        Id = option.Id; Name = option.Name; Description = option.Description;
        optionImagePath = images?.ImagePath;
        Choices = option.Choices.Select((choice, index) => new OptionChoiceItem(choice.Name,
            images?.Choices.ElementAtOrDefault(index)?.Description ?? "", images?.Choices.ElementAtOrDefault(index)?.ImagePath)).ToArray();
        enabled = selection.Enabled; choiceIndex = selection.ChoiceIndex;
    }
}
