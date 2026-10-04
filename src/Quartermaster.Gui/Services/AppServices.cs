using Quartermaster.Library;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Storage;
using Quartermaster.Library.Profiles;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Services;

public sealed class AppServices : IAsyncDisposable
{
    public bool IsDisposed { get; private set; }
    public ModDownloads Downloads { get; }
    public Func<string, CancellationToken, Task<Quartermaster.Providers.Providers.NexusMods.NexusUser>> ValidateNexusKeyAsync { get; }
    public Quartermaster.Providers.ProviderManager Providers { get; }
    public Quartermaster.Providers.ApiKeyStore Keys { get; }
    public Quartermaster.Providers.Providers.NexusMods.NexusClient Nexus { get; }
    public Quartermaster.Providers.Providers.GitHub.GitHubProvider GitHub { get; }
    public Quartermaster.Library.Mods.LibraryService Library { get; }
    public string DataDirectory { get; }
    public LibrarySession Session { get; }
    public OperationState Operations { get; }
    public Action LaunchGame { get; }
    public Action<Uri> OpenBrowser { get; }
    public IDialogService Dialogs { get; }
    public AppServices(string directory, IDialogService dialogs, Func<IReadOnlyList<string>>? discover = null, Action? launchGame = null, Action<Uri>? openBrowser = null, HttpClient? nexusApi = null, HttpClient? nexusDownloads = null, HttpClient? githubApi = null, HttpClient? githubDownloads = null)
    {
        DataDirectory = Path.GetFullPath(directory); Dialogs = dialogs;
        var log = new JsonEventLog(DataDirectory);
        Operations = new(log.AppendAsync);
        LaunchGame = launchGame ?? (() => global::System.Diagnostics.Process.Start(
            new global::System.Diagnostics.ProcessStartInfo("steam://rungameid/553850") { UseShellExecute = true }));
        OpenBrowser = uri =>
        {
            if (!uri.IsAbsoluteUri || uri.Scheme != "https" || uri.UserInfo.Length != 0) throw new ArgumentException("Only public HTTPS pages can be opened.");
            if (openBrowser is not null) openBrowser(uri);
            else global::System.Diagnostics.Process.Start(new global::System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        };
        var content = new ModContentStore(DataDirectory);
        var store = new JsonLibraryStore(DataDirectory);
        Library = new LibraryService(store, content);
        Keys = new(DataDirectory);
        Nexus = new(ct => Keys.GetAsync("nexusmods", ct), nexusApi, nexusDownloads);
        ValidateNexusKeyAsync = async (key, ct) =>
        {
            using var client = new Quartermaster.Providers.Providers.NexusMods.NexusClient(_ => Task.FromResult<string?>(key), nexusApi);
            return await client.ValidateAsync(ct);
        };
        GitHub = new(githubApi, githubDownloads, ct => Keys.GetAsync("github", ct));
        Providers = new(Library, new(DataDirectory), [new Quartermaster.Providers.Providers.NexusMods.NexusAdapter(Nexus), GitHub], TemporaryStorage.PathFor(DataDirectory, "downloads"), OpenBrowser);
        Session = new(Library, new ProfileArchives(store, content),
            new FileDeploymentStorage(DataDirectory, content), content, new SettingsStore(DataDirectory),
            discover ?? (() => SteamGameDiscovery.FindInstallations()));
        Downloads = new(this);
    }
    public async ValueTask DisposeAsync() { IsDisposed = true; await Providers.DisposeAsync().ConfigureAwait(false); Nexus.Dispose(); GitHub.Dispose(); }
    public static string DefaultDataDirectory => Environment.GetEnvironmentVariable("QUARTERMASTER_DATA_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Quartermaster");
}
