using Quartermaster.Core.Deployment;
using Quartermaster.Core.Patching;
using Quartermaster.Library;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Gui.Shared;
using Quartermaster.Library.Importing;

namespace Quartermaster.Gui.Services;

/// <summary>Shared loaded state. Backend work runs on workers; notifications return to the UI caller.</summary>
public sealed class LibrarySession(LibraryService library, IDeploymentStorage storage, ModContentStore contents, SettingsStore settingsStore,
    Func<IReadOnlyList<string>> discover) : ViewModelBase
{
    public event EventHandler? Changed;
    public LibraryState State { get; private set; } = LibraryState.Empty;
    public ApplicationSettings Settings { get; private set; } = new();
    public DeploymentInspection? Inspection { get; private set; }
    public string DeploymentProblem { get; private set; } = "";
    public Profile? ActiveProfile => State.Profiles.FirstOrDefault(p => p.Id == State.ActiveProfileId);
    public string GameDirectory => Settings.GameDataDirectory ?? "";
    public string DeploymentStatus => GameDirectory == "" ? "Choose your game folder in Settings" :
        DeploymentProblem != "" ? DeploymentProblem : Inspection is null ? "Not inspected" :
        Inspection.NeedsPurge ? "Deployment incomplete or unknown. Purge patches, then redeploy." :
        Inspection.Ledger.Files.Count == 0 ? "No managed patches deployed" : $"{Inspection.Ledger.Files.Count} managed files deployed";
    public string DeployedProfileName => Inspection?.Ledger.SelectionName ?? State.Profiles.FirstOrDefault(p => p.Id == Inspection?.Ledger.SelectionId)?.Name ?? "None";

    public async Task InitializeAsync(CancellationToken ct)
    {
        Settings = await Task.Run(() => settingsStore.LoadAsync(ct), ct);
        State = await Task.Run(() => library.LoadAsync(ct), ct);
        if (State.Profiles.Count == 0)
        {
            await Task.Run(() => library.SaveProfileAsync(ProfileEditor.Create("Default"), true, ct), ct);
            State = await Task.Run(() => library.LoadAsync(ct), ct);
        }
        if (GameDirectory == "")
        {
            var found = await Task.Run(discover, ct);
            if (found.Count == 1)
            {
                Settings = Settings with { GameDataDirectory = found[0] };
                await Task.Run(() => settingsStore.SaveAsync(Settings, ct), ct);
            }
        }
        await RefreshInspectionAsync(ct);
        Publish();
    }
    public async Task ReloadAsync(CancellationToken ct)
    {
        State = await Task.Run(() => library.LoadAsync(ct), ct);
        await RefreshInspectionAsync(ct); Publish();
    }
    private void Publish() { Notify(nameof(State)); Notify(nameof(GameDirectory)); Notify(nameof(DeploymentStatus)); Changed?.Invoke(this, EventArgs.Empty); }
    public async Task ImportAsync(string source, CancellationToken ct)
    {
        await Task.Run(() => library.ImportAsync(source, cancellationToken: ct), ct);
        await ReloadAsync(CancellationToken.None);
    }
    public async Task RemoveModAsync(Guid id, CancellationToken ct)
    {
        await Task.Run(() => library.RemoveAsync(id, ct), ct);
        await ReloadAsync(CancellationToken.None);
    }
    public async Task SaveProfileAsync(Profile profile, bool active, CancellationToken ct)
    {
        await Task.Run(() => library.SaveProfileAsync(profile, active, ct), ct);
        await ReloadAsync(CancellationToken.None);
    }
    public async Task DeleteProfileAsync(Guid id, CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            await library.RemoveProfileAsync(id, ct);
            var state = await library.LoadAsync(CancellationToken.None);
            if (state.ActiveProfileId is null && state.Profiles.FirstOrDefault() is { } next)
                await library.SaveProfileAsync(next, true, CancellationToken.None);
        }, ct);
        await ReloadAsync(CancellationToken.None);
    }
    public Task<IReadOnlyList<string>> DiscoverAsync(CancellationToken ct) => Task.Run(discover, ct);
    public async Task SetGameDirectoryAsync(string path, CancellationToken ct)
    {
        var resolved = await Task.Run(() => SteamGameDiscovery.ResolveDataDirectory(path), ct)
            ?? throw new ArgumentException("Choose a Helldivers 2 installation or its data folder.");
        var settings = Settings with { GameDataDirectory = resolved };
        await Task.Run(() => settingsStore.SaveAsync(settings, ct), ct);
        Settings = settings;
        await RefreshInspectionAsync(ct); Publish();
    }
    private async Task RefreshInspectionAsync(CancellationToken ct)
    {
        Inspection = null; DeploymentProblem = "";
        if (GameDirectory == "") return;
        try { Inspection = await Task.Run(() => new DeploymentService(storage).InspectAsync(GameDirectory, ct), ct); }
        catch (Exception ex) when (ex is IOException or ArgumentException) { DeploymentProblem = ex.Message; }
    }
    private string RequireGame() => GameDirectory != "" ? GameDirectory : throw new InvalidOperationException("Choose your game folder in Settings first.");
    public async Task DeployAsync(Guid profileId, IDialogService dialogs, CancellationToken ct)
    {
        var request = ProfilePatches.Resolve(State, profileId); var target = RequireGame();
        IPatchRepairer? adapter = null;
        if (Settings.Repatch != RepatchMode.Never && request.Patches.Count > 0)
        {
            adapter = await Task.Run(() => new RepatcherAdapter(GameArchives.Open(target, ct)), ct);
            var affected = await Task.Run(async () =>
            {
                var changed = new HashSet<Guid>();
                foreach (var patch in request.Patches)
                {
                    var file = patch.Files.Single(f => f.Kind == PatchFileKind.Main);
                    var original = await contents.ReadVerifiedAsync(patch.SourceId, file, ct);
                    var result = adapter.Repair(original, ct);
                    if (result.RemovedUnits > 0) throw new InvalidDataException("Repatching would remove missing units. Use an updated mod.");
                    if (!original.AsSpan().SequenceEqual(result.Data)) changed.Add(patch.SourceId);
                }
                return changed;
            }, ct);
            if (affected.Count == 0) adapter = null;
            else if (Settings.Repatch == RepatchMode.Ask)
            {
                var names = string.Join("\n", State.Mods.Where(m => affected.Contains(m.Id)).Select(m => m.Name));
                if (!await dialogs.ConfirmAsync("Repatch required", $"These mods need repatching for the installed game:\n\n{names}\n\nRepatch and deploy? Originals will remain unchanged.", "Repatch and deploy")) return;
            }
        }
        ct.ThrowIfCancellationRequested();
        try
        {
            await Task.Run(async () =>
            {
                var repair = adapter is not null;
                await new DeploymentService(storage, adapter).DeployAsync(request, target, new(Repatch: repair), ct);
            }, ct);
        }
        finally { await ReloadAsync(CancellationToken.None); }
    }
    public async Task SetRepatchModeAsync(RepatchMode mode, CancellationToken ct)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid repatch setting.");
        var settings = Settings with { Repatch = mode };
        await Task.Run(() => settingsStore.SaveAsync(settings, ct), ct);
        Settings = settings; Publish();
    }
    public Task ExportRepatchedAsync(Mod mod, string destination, CancellationToken ct)
    {
        var game = RequireGame();
        var relative = Path.GetRelativePath(game, Path.GetFullPath(destination));
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new ArgumentException("Export outside the game directory.");
        return Task.Run(() => contents.ExportRepatchedAsync(mod, destination, new RepatcherAdapter(GameArchives.Open(game, ct)), ct), ct);
    }
    public async Task PurgeAsync(CancellationToken ct)
    {
        try { await Task.Run(() => new DeploymentService(storage).PurgeAsync(RequireGame(), ct), ct); }
        finally { await ReloadAsync(CancellationToken.None); }
    }
    public async Task<string?> GetLaunchWarningAsync(Guid profileId, CancellationToken ct)
    {
        await RefreshInspectionAsync(ct); Publish();
        if (DeploymentProblem != "") return DeploymentProblem + " Purge patches, then redeploy.";
        if (Inspection is null) return "Deployment could not be inspected. Choose a game folder in Settings.";
        if (Inspection.NeedsPurge) return "Deployment is incomplete or unknown. Purge patches, then redeploy before running the game.";
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(State, profileId));
        if (Inspection.Ledger.SelectionId != profileId || Inspection.Ledger.Signature != plan.Signature)
            return $"The selected profile or its loadout is not deployed. Currently deployed profile: {DeployedProfileName}. Deploy the selected profile before running, or continue with the installed loadout.";
        return null;
    }
}
