using Quartermaster.Interop.Arsenal;
using Quartermaster.Interop.Echelon;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Settings;

public sealed partial class SettingsViewModel
{
    private string arsenalDirectory = ArsenalReader.DefaultDirectory;
    public string ArsenalDirectory
    {
        get => arsenalDirectory;
        set { if (Set(ref arsenalDirectory, value)) ImportArsenalCommand.Refresh(); }
    }
    public bool ShowImports => Matches("Imports Arsenal Echelon import profiles mods migration folder data");
    public AsyncCommand ImportArsenalCommand { get; private set; } = null!;
    public AsyncCommand BrowseArsenalCommand { get; private set; } = null!;

    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> discoverEchelonDirectories;
    private string echelonDirectory = "";
    private bool echelonDirectoryEdited;
    public string EchelonDirectory
    {
        get => echelonDirectory;
        set
        {
            echelonDirectoryEdited = true;
            if (!Set(ref echelonDirectory, value)) return;
            Notify(nameof(SelectedEchelonDirectory));
            ImportEchelonCommand.Refresh();
        }
    }
    public IReadOnlyList<string> EchelonDirectories { get; private set; } = [];
    public bool HasMultipleEchelonDirectories => EchelonDirectories.Count > 1;
    public string? SelectedEchelonDirectory
    {
        get => EchelonDirectories.FirstOrDefault(path => string.Equals(path, EchelonDirectory,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
        set { if (value is not null) EchelonDirectory = value; }
    }
    public async Task RefreshEchelonDirectoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var directories = await discoverEchelonDirectories(ct);
            ct.ThrowIfCancellationRequested();
            EchelonDirectories = directories;
            if (!echelonDirectoryEdited && directories.Count == 1) EchelonDirectory = directories[0];
            Notify(nameof(EchelonDirectories));
            Notify(nameof(HasMultipleEchelonDirectories));
            Notify(nameof(SelectedEchelonDirectory));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public AsyncCommand ImportEchelonCommand { get; private set; } = null!;
    public AsyncCommand BrowseEchelonCommand { get; private set; } = null!;

    private void InitializeInteropCommands()
    {
        BrowseArsenalCommand = Operations.CreateCommand("Selecting Arsenal folder", async _ =>
        {
            var path = await Services.Dialogs.PickFolderAsync(Localizer.Text("Choose the Arsenal data folder"));
            if (path is not null) ArsenalDirectory = path;
        });
        ImportArsenalCommand = Operations.CreateCommand("Importing Arsenal profiles", async ct =>
        {
            var plan = await Task.Run(() => ArsenalReader.ReadAsync(ArsenalDirectory.Trim(), ct: ct), ct);
            var names = string.Join("\n", plan.Profiles.Select(profile => "• " + profile.Name));
            var message = Localizer.Interpolate($"Import {plan.Profiles.Count} profiles with {plan.Mods.Count} mods and {plan.GroupCount} groups?\n\n{names}\n\nMod files will be copied into Quartermaster.");
            if (!await Services.Dialogs.ConfirmAsync(Localizer.Text("Import Arsenal profiles"), message, Localizer.Text("Import"))) return;
            var progress = Operations.CreateProgress<ArsenalImportProgress>(value =>
                Localizer.Interpolate($"Importing Arsenal mod {value.Current} of {value.Total}: {value.Name}"));
            try { await Task.Run(() => Services.ArsenalImporter.ImportAsync(plan, progress, ct), ct); }
            finally { await Session.ReloadAsync(CancellationToken.None); }
        }, () => !string.IsNullOrWhiteSpace(ArsenalDirectory));
        BrowseEchelonCommand = Operations.CreateCommand("Selecting Echelon folder", async _ =>
        {
            var path = await Services.Dialogs.PickFolderAsync(Localizer.Text("Choose the Echelon data folder"));
            if (path is not null) EchelonDirectory = path;
        });
        ImportEchelonCommand = Operations.CreateCommand("Importing Echelon profiles", async ct =>
        {
            var plan = await Task.Run(() => EchelonReader.ReadAsync(EchelonDirectory.Trim(), ct: ct), ct);
            var names = string.Join("\n", plan.Profiles.Select(profile => "• " + profile.Name));
            var message = Localizer.Interpolate($"Import {plan.Profiles.Count} profiles with {plan.Mods.Count} mods and {plan.GroupCount} groups?\n\n{names}\n\nMod files will be copied into Quartermaster.");
            if (!await Services.Dialogs.ConfirmAsync(Localizer.Text("Import Echelon profiles"), message, Localizer.Text("Import"))) return;
            var progress = Operations.CreateProgress<EchelonImportProgress>(value =>
                Localizer.Interpolate($"Importing Echelon mod {value.Current} of {value.Total}: {value.Name}"));
            try { await Task.Run(() => Services.EchelonImporter.ImportAsync(plan, progress, ct), ct); }
            finally { await Session.ReloadAsync(CancellationToken.None); }
        }, () => !string.IsNullOrWhiteSpace(EchelonDirectory));
    }
}
