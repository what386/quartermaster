using Quartermaster.Interop.Arsenal;
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
    public bool ShowImports => Matches("Imports Arsenal import profiles mods migration folder data");
    public AsyncCommand ImportArsenalCommand { get; private set; } = null!;
    public AsyncCommand BrowseArsenalCommand { get; private set; } = null!;

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
    }
}
