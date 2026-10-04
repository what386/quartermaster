using Quartermaster.Library.Mods;
using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Mods;

public sealed record ModListItem(Mod Mod, int Index, bool HasConflict, string? IconPath = null) : IModRow
{
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
    public IReadOnlyList<ModListItem> Mods { get; private set; } = [];
    public string Search { get => search; set { if (Set(ref search, value)) Refresh(); } }
    public ModListItem? SelectedMod
    {
        get => selected;
        set { if (Set(ref selected, value)) { Notify(nameof(Details)); Notify(nameof(HasSelection)); RemoveCommand.Refresh(); ExportRepatchedCommand.Refresh(); } }
    }
    public ModDetailsViewModel? Details => SelectedMod is { } item ? new(item.Mod) : null;
    public bool HasMods => Session.State.Mods.Count > 0;
    public bool HasVisibleMods => Mods.Count > 0;
    public string EmptyMessage => HasMods ? "No mods match your search." : "Import a ZIP or folder to add mods to your library.";
    public bool HasSelection => SelectedMod is not null;
    public string CountLabel => $"{Mods.Count} mods";
    public AsyncCommand ImportZipCommand { get; }
    public AsyncCommand ImportFolderCommand { get; }
    public AsyncCommand RemoveCommand { get; }
    public AsyncCommand ExportRepatchedCommand { get; }
    public ModsViewModel(AppServices services) : base(services)
    {
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
            var mod = SelectedMod!.Mod;
            var name = string.Concat(mod.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' || c == '\\' ? '_' : c));
            var path = await Services.Dialogs.SaveModZipAsync(name + "-repatched.zip");
            if (path is not null) await Session.ExportRepatchedAsync(mod, path, ct);
        }, () => SelectedMod is not null && Session.GameDirectory != "");
        RemoveCommand = Operations.CreateCommand("Removing mod", async ct =>
        {
            var mod = SelectedMod!.Mod;
            if (await Services.Dialogs.ConfirmAsync("Remove mod", $"Remove {mod.Name} from your library and profiles? Deployed files remain until you redeploy or purge.", "Remove"))
                await Session.RemoveModAsync(mod.Id, ct);
        }, () => SelectedMod is not null);
        WatchSession();
    }
    public IReadOnlyList<Quartermaster.Library.Profiles.Profile> Profiles => Session.State.Profiles;
    public Task AddToProfileAsync(Guid modId, Guid profileId) => Operations.RunAsync("Adding mod to profile",
        ct => Session.AddModToProfileAsync(modId, profileId, ct));
    protected override void Refresh()
    {
        var id = selected?.Mod.Id;
        var collisions = Session.ActiveProfile is { } active ? Quartermaster.Core.Deployment.ConflictAnalyzer.Analyze(
            Quartermaster.Library.Profiles.ProfilePatches.Resolve(Session.State, active)).Resources.SelectMany(c => c.SourceIds).ToHashSet() : [];
        Mods = Session.State.Mods.Where(m => m.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Select((m, index) => new ModListItem(m, index, collisions.Contains(m.Id), Session.GetIconPath(m))).ToArray();
        Notify(nameof(Mods)); Notify(nameof(CountLabel)); Notify(nameof(HasMods)); Notify(nameof(HasVisibleMods)); Notify(nameof(EmptyMessage));
        SelectedMod = Mods.FirstOrDefault(m => m.Mod.Id == id) ?? Mods.FirstOrDefault();
        ExportRepatchedCommand.Refresh();
    }
}
