using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Downloads;
using Quartermaster.Providers.Clients.NexusMods;
using Quartermaster.Providers.Protocol;
using Quartermaster.Providers.Clients.GitHub;

namespace Quartermaster.Gui.Settings;

public sealed partial class SettingsViewModel
{
    private string nexusApiKey = "";
    private bool removeNexusKey;
    private bool hasSavedNexusKey;
    private string nexusAccount = "Not connected";
    private string downloadFolder = "";
    private string savedDownloadFolder = "";
    private string githubToken = "";
    private bool removeGitHubToken;
    private bool hasSavedGitHubToken;
    private string githubAccount = "Optional · Public API access";
    public string GitHubToken { get => githubToken; set { if (Set(ref githubToken, value)) SaveCommand.Refresh(); } }
    public bool RemoveGitHubToken { get => removeGitHubToken; set { if (Set(ref removeGitHubToken, value)) SaveCommand.Refresh(); } }
    public bool HasSavedGitHubToken { get => hasSavedGitHubToken; private set => Set(ref hasSavedGitHubToken, value); }
    public string GitHubAccount { get => githubAccount; private set => Set(ref githubAccount, value); }
    public bool ShowGitHub => Matches("GitHub providers account API key personal access token authentication rate limits");
    public string NexusApiKey { get => nexusApiKey; set { if (Set(ref nexusApiKey, value)) SaveCommand.Refresh(); } }
    public bool RemoveNexusKey { get => removeNexusKey; set { if (Set(ref removeNexusKey, value)) SaveCommand.Refresh(); } }
    public bool HasSavedNexusKey { get => hasSavedNexusKey; private set => Set(ref hasSavedNexusKey, value); }
    public string NexusAccount { get => nexusAccount; private set => Set(ref nexusAccount, value); }
    public string DownloadFolder { get => downloadFolder; set { if (Set(ref downloadFolder, value)) SaveCommand.Refresh(); } }
    public bool ShowNexus => Matches("Nexus Mods providers account API key authentication nxm");
    private bool ProviderDraftChanged => !string.IsNullOrWhiteSpace(NexusApiKey) || RemoveNexusKey && HasSavedNexusKey ||
        !string.IsNullOrWhiteSpace(GitHubToken) || RemoveGitHubToken && HasSavedGitHubToken || DownloadFolder != savedDownloadFolder;
    public AsyncCommand BrowseDownloadsCommand { get; private set; } = null!;
    public AsyncCommand RegisterNxmCommand { get; private set; } = null!;
    public AsyncCommand OpenNexusKeySettingsCommand { get; private set; } = null!;
    public AsyncCommand OpenGitHubTokenSettingsCommand { get; private set; } = null!;
    private void InitializeProviderCommands()
    {
        BrowseDownloadsCommand = Operations.CreateCommand("Selecting download folder", async _ =>
        {
            var path = await Services.Dialogs.PickFolderAsync("Choose your browser's download folder");
            if (path is not null) DownloadFolder = path;
        });
        RegisterNxmCommand = Operations.CreateCommand("Registering nxm links", NxmProtocol.RegisterAsync);
        OpenNexusKeySettingsCommand = Operations.CreateCommand("Opening Nexus API settings", _ =>
        {
            Services.OpenBrowser(new("https://www.nexusmods.com/settings/api-keys"));
            return Task.CompletedTask;
        });
        OpenGitHubTokenSettingsCommand = Operations.CreateCommand("Opening GitHub token settings", _ =>
        {
            Services.OpenBrowser(new("https://github.com/settings/personal-access-tokens"));
            return Task.CompletedTask;
        });
    }
    public async Task InitializeProviderSettingsAsync(CancellationToken ct)
    {
        HasSavedNexusKey = await Services.Keys.GetAsync(NexusAdapter.ProviderId, ct) is not null;
        NexusAccount = HasSavedNexusKey ? "Personal API key saved" : "Not connected";
        HasSavedGitHubToken = !string.IsNullOrWhiteSpace(await Services.Keys.GetAsync(GitHubProvider.ProviderId, ct));
        GitHubAccount = HasSavedGitHubToken ? "Personal access token saved" : "Optional · Public API access";
        savedDownloadFolder = Services.Providers.State.Directories.FirstOrDefault() ?? DownloadStore.DefaultDownloadsDirectory;
        DownloadFolder = savedDownloadFolder;
    }
    private async Task<(NexusUser? Nexus, string? GitHub)> ValidateProviderDraftAsync(CancellationToken ct)
    {
        if (DownloadFolder != savedDownloadFolder && (string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder.Trim())))
            throw new ArgumentException("Choose an absolute download folder path.");
        var nexus = !RemoveNexusKey && !string.IsNullOrWhiteSpace(NexusApiKey)
            ? await Services.ValidateNexusKeyAsync(NexusApiKey.Trim(), ct) : null;
        var github = !RemoveGitHubToken && !string.IsNullOrWhiteSpace(GitHubToken)
            ? await Services.GitHub.ValidateTokenAsync(GitHubToken.Trim(), ct) : null;
        return (nexus, github);
    }
    private async Task SaveProviderDraftAsync((NexusUser? Nexus, string? GitHub) accounts, CancellationToken ct)
    {
        var user = accounts.Nexus;
        if (RemoveNexusKey)
        {
            await Services.Keys.SetAsync(NexusAdapter.ProviderId, null, ct);
            HasSavedNexusKey = false; NexusAccount = "Not connected";
        }
        else if (user is not null)
        {
            await Services.Keys.SetAsync(NexusAdapter.ProviderId, NexusApiKey.Trim(), ct);
            HasSavedNexusKey = true; NexusAccount = $"{user.Name} · {(user.IsPremium ? "Premium" : "Free account")}";
        }
        NexusApiKey = ""; RemoveNexusKey = false;
        if (RemoveGitHubToken)
        {
            await Services.Keys.SetAsync(GitHubProvider.ProviderId, null, ct);
            HasSavedGitHubToken = false; GitHubAccount = "Optional · Public API access";
        }
        else if (accounts.GitHub is { } login)
        {
            await Services.Keys.SetAsync(GitHubProvider.ProviderId, GitHubToken.Trim(), ct);
            HasSavedGitHubToken = true; GitHubAccount = login;
        }
        GitHubToken = ""; RemoveGitHubToken = false;
        if (DownloadFolder != savedDownloadFolder)
        {
            await Services.Providers.SetDirectoriesAsync([DownloadFolder.Trim()], ct);
            savedDownloadFolder = Services.Providers.State.Directories[0]; DownloadFolder = savedDownloadFolder;
        }
    }
    private void ResetProviderDraft()
    {
        NexusApiKey = ""; RemoveNexusKey = HasSavedNexusKey;
        GitHubToken = ""; RemoveGitHubToken = HasSavedGitHubToken;
        DownloadFolder = DownloadStore.DefaultDownloadsDirectory;
    }
}
