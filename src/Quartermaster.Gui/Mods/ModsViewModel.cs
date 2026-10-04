using Quartermaster.Library.Mods;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;
using System.Collections.ObjectModel;

namespace Quartermaster.Gui.Mods;

public sealed record ModListItem(Mod Mod, int Index, bool HasConflict, string? IconPath = null) : IModRow
{
    public AsyncCommand? UpdateCommand { get; init; }
    public string? UpdateDescription { get; init; }
    public bool HasUpdate => UpdateCommand is not null;
    public string Name => Mod.Name;
    public string Title => ModPresentation.Title(Mod);
    public string Number => (Index + 1).ToString();
    public string Description => ModPresentation.Description(Mod);
    public string Monogram => ModPresentation.Monogram(Mod);
    public bool IsLoaded => false;
    public bool IsUnloaded => false;
    public bool HasDeploymentWarning => false;
    public string? DeploymentDescription => null;
    public bool HasOptions => Mod.Options.Count > 0;
    public bool HasToggle => false;
    public bool IsEnabled => false;
    public Avalonia.Layout.HorizontalAlignment KnobAlignment => Avalonia.Layout.HorizontalAlignment.Left;
    public string ToggleDescription => "";
    public AsyncCommand? EnableCommand => null;
}

public interface IModRow
{
    bool HasUpdate { get; }
    string? UpdateDescription { get; }
    AsyncCommand? UpdateCommand { get; }
    string Name { get; }
    string Number { get; }
    string Description { get; }
    string Monogram { get; }
    string Title { get; }
    string? IconPath { get; }
    bool HasConflict { get; }
    bool HasToggle { get; }
    bool HasOptions { get; }
    bool IsLoaded { get; }
    bool IsUnloaded { get; }
    bool HasDeploymentWarning { get; }
    string? DeploymentDescription { get; }
    bool IsEnabled { get; }
    Avalonia.Layout.HorizontalAlignment KnobAlignment { get; }
    string ToggleDescription { get; }
    AsyncCommand? EnableCommand { get; }
}

public static class ModPresentation
{
    public static string Count(int count, string singular) => $"{count} {singular}{(count == 1 ? "" : "s")}";
    public static string Title(Mod mod) => string.IsNullOrWhiteSpace(mod.Version) ? mod.Name : $"{mod.Name} · {mod.Version}";
    public static string Description(Mod mod) => string.IsNullOrWhiteSpace(mod.Description) ? "No description" :
        string.Join(" ", mod.Description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public static string Monogram(Mod mod) => string.Concat(mod.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(s => char.ToUpperInvariant(s[0])));
}

public sealed class ModsViewModel : SessionViewModel
{
    private string search = "";
    private ModListItem? selected;
    public ObservableCollection<ModListItem> SelectedMods { get; } = [];
    public IReadOnlyList<ModListItem> Mods { get; private set; } = [];
    public string Search { get => search; set { if (Set(ref search, value)) Refresh(); } }
    public ModListItem? SelectedMod
    {
        get => selected;
        set
        {
            if (value == selected && SelectedMods.Count <= 1) return;
            SelectedMods.Clear();
            if (value is not null) SelectedMods.Add(value);
        }
    }
    public ModDetailsViewModel? Details => SelectedMods.Count == 1 ? new(SelectedMods[0].Mod) : null;
    public bool HasMods => Session.State.Mods.Count > 0;
    public bool HasVisibleMods => Mods.Count > 0;
    public string EmptyMessage => HasMods ? "No mods match your search." : "Add a mod link, ZIP or folder to your library.";
    public bool HasSelection => SelectedMods.Count > 0;
    public bool HasSingleSelection => SelectedMods.Count == 1;
    public string CountLabel => $"{Mods.Count} mods";
    public ModDownloads Downloads => Services.Downloads;
    public AsyncCommand AddModCommand { get; }
    public AsyncCommand CheckUpdatesCommand { get; }
    public AsyncCommand ImportZipCommand { get; }
    public AsyncCommand ImportFolderCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ExportRepatchedCommand { get; }
    public ModsViewModel(AppServices services) : base(services)
    {
        AddModCommand = Operations.CreateCommand("Adding mod", ct => Services.Downloads.AddAsync(ct));
        CheckUpdatesCommand = Operations.CreateCommand("Checking mod updates", ct => Services.Downloads.CheckUpdatesAsync(ct));
        Services.Downloads.Changed += (_, _) => { foreach (var row in Mods) row.UpdateCommand?.Refresh(); };
        Operations.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OperationState.IsBusy)) foreach (var row in Mods) row.UpdateCommand?.Refresh(); };
        ImportZipCommand = Operations.CreateCommand("Importing mod", async ct =>
        {
            var path = await Services.Dialogs.PickModZipAsync();
            if (path is not null) await Session.ImportAsync(path, ct);
        });
        ImportFolderCommand = Operations.CreateCommand("Importing mod", async ct =>
        {
            var path = await Services.Dialogs.PickFolderAsync("Import mod folder");
            if (path is not null) await Session.ImportAsync(path, ct);
        });
        ExportRepatchedCommand = Operations.CreateCommand("Exporting repatched mod", async ct =>
        {
            var mod = SelectedMods.Single().Mod;
            var name = string.Concat(mod.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c));
            var path = await Services.Dialogs.SaveModZipAsync(name + "-repatched.zip");
            if (path is not null) await Session.ExportRepatchedAsync(mod, path, ct);
        }, () => HasSingleSelection && Session.GameDirectory != "");
        RemoveCommand = Operations.CreateCommand("Removing mods", async ct =>
        {
            var mods = SelectedMods.Select(item => item.Mod).ToArray();
            var names = string.Join("\n", mods.Select(mod => mod.Name));
            if (await Services.Dialogs.ConfirmAsync(mods.Length == 1 ? "Remove mod" : $"Remove {mods.Length} mods",
                $"Remove these mods from your library and profiles?\n\n{names}\n\nDeployed files remain until you redeploy or purge.", "Remove"))
                await Session.RemoveModsAsync(mods.Select(mod => mod.Id).ToArray(), ct);
        }, () => HasSelection);
        SelectedMods.CollectionChanged += (_, _) => SelectionChanged();
        WatchSession();
    }
    public IReadOnlyList<Quartermaster.Library.Profiles.Profile> Profiles => Session.State.Profiles;
    public Task AddToProfileAsync(Guid modId, Guid profileId) => Operations.RunAsync("Adding mod to profile",
        ct => Session.AddModToProfileAsync(modId, profileId, ct));
    public Task AddSelectedToProfileAsync(Guid profileId)
    {
        var ids = SelectedMods.Select(item => item.Mod.Id).ToArray();
        return Operations.RunAsync("Adding mods to profile", ct => Session.AddModsToProfileAsync(ids, profileId, ct));
    }
    private void SelectionChanged()
    {
        Set(ref selected, SelectedMods.FirstOrDefault(), nameof(SelectedMod));
        Notify(nameof(Details)); Notify(nameof(HasSelection)); Notify(nameof(HasSingleSelection));
        RemoveCommand.Refresh(); ExportRepatchedCommand.Refresh();
    }
    protected override void Refresh()
    {
        var ids = SelectedMods.Select(item => item.Mod.Id).ToHashSet();
        var collisions = Session.ActiveProfile is { } active ? Quartermaster.Core.Deployment.ConflictAnalyzer.Analyze(
            Quartermaster.Library.Profiles.ProfilePatches.Resolve(Session.State, active)).Resources.SelectMany(c => c.SourceIds).ToHashSet() : [];
        Mods = Session.State.Mods.Where(m => m.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select((m, index) => new ModListItem(m, index, collisions.Contains(m.Id), Session.GetIconPath(m))
            { UpdateCommand = Services.Downloads.CreateUpdateCommand(m), UpdateDescription = Services.Downloads.UpdateDescription(m) }).ToArray();
        Notify(nameof(Mods)); Notify(nameof(CountLabel)); Notify(nameof(HasMods)); Notify(nameof(HasVisibleMods)); Notify(nameof(EmptyMessage));
        SelectedMods.Clear();
        foreach (var item in Mods.Where(item => ids.Contains(item.Mod.Id))) SelectedMods.Add(item);
        if (SelectedMods.Count == 0 && Mods.FirstOrDefault() is { } first) SelectedMods.Add(first);
        SelectionChanged();
    }
}
