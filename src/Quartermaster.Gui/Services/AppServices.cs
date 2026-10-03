using Quartermaster.Library;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Services;

public sealed class AppServices
{
    public string DataDirectory { get; }
    public LibrarySession Session { get; }
    public OperationState Operations { get; }
    public Action LaunchGame { get; }
    public IDialogService Dialogs { get; }
    public AppServices(string directory, IDialogService dialogs, Func<IReadOnlyList<string>>? discover = null, Action? launchGame = null)
    {
        DataDirectory = Path.GetFullPath(directory); Dialogs = dialogs;
        var log = new JsonEventLog(DataDirectory);
        Operations = new(log.AppendAsync);
        LaunchGame = launchGame ?? (() => global::System.Diagnostics.Process.Start(
            new global::System.Diagnostics.ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true }));
        var content = new ModContentStore(DataDirectory);
        Session = new(new LibraryService(new JsonLibraryStore(DataDirectory), content),
            new FileDeploymentStorage(DataDirectory, content), new SettingsStore(DataDirectory),
            discover ?? (() => SteamGameDiscovery.FindInstallations()));
    }
    public static string DefaultDataDirectory => Environment.GetEnvironmentVariable("QUARTERMASTER_DATA_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");
}
