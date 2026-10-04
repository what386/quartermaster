using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Downloads;
using Quartermaster.Providers.Providers.NexusMods;
using Quartermaster.Providers.Protocol;

namespace Quartermaster.Gui.Settings;

public sealed partial class SettingsViewModel
{
    private string nexusApiKey = "";
    private bool removeNexusKey;
    private bool hasSavedNexusKey;
    private string nexusAccount = "Not connected";
    private string downloadFolder = "";
    private string savedDownloadFolder = "";
    public string NexusApiKey { get => nexusApiKey; set { if (Set(ref nexusApiKey, value)) SaveCommand.Refresh(); } }
    public bool RemoveNexusKey { get => removeNexusKey; set { if (Set(ref removeNexusKey, value)) SaveCommand.Refresh(); } }
    public bool HasSavedNexusKey { get => hasSavedNexusKey; private set => Set(ref hasSavedNexusKey, value); }
    public string NexusAccount { get => nexusAccount; private set => Set(ref nexusAccount, value); }
    public string DownloadFolder { get => downloadFolder; set { if (Set(ref downloadFolder, value)) SaveCommand.Refresh(); } }
    public bool ShowNexus => Matches("Nexus Mods provider account API key authentication nxm downloads browser folder");
    private bool ProviderDraftChanged => !string.IsNullOrWhiteSpace(NexusApiKey) || RemoveNexusKey && HasSavedNexusKey || DownloadFolder != savedDownloadFolder;
    public AsyncCommand BrowseDownloadsCommand { get; private set; } = null!;
    public AsyncCommand RegisterNxmCommand { get; private set; } = null!;
    private void InitializeProviderCommands()
    {
        BrowseDownloadsCommand = Operations.CreateCommand("Selecting download folder", async _ =>
        {
            var path = await Services.Dialogs.PickFolderAsync("Choose your browser's download folder");
            if (path is not null) DownloadFolder = path;
        });
        RegisterNxmCommand = Operations.CreateCommand("Registering nxm links", NxmProtocol.RegisterAsync);
    }
    public async Task InitializeProviderSettingsAsync(CancellationToken ct)
    {
        HasSavedNexusKey = await Services.Keys.GetAsync(NexusAdapter.ProviderId, ct) is not null;
        NexusAccount = HasSavedNexusKey ? "Personal API key saved" : "Not connected";
        savedDownloadFolder = Services.Providers.State.Directories.FirstOrDefault() ?? DownloadStore.DefaultDownloadsDirectory;
        DownloadFolder = savedDownloadFolder;
    }
    private async Task<NexusUser?> ValidateProviderDraftAsync(CancellationToken ct)
    {
        if (DownloadFolder != savedDownloadFolder && (string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder.Trim())))
            throw new ArgumentException("Choose an absolute download folder path.");
        return !RemoveNexusKey && !string.IsNullOrWhiteSpace(NexusApiKey)
            ? await Services.ValidateNexusKeyAsync(NexusApiKey.Trim(), ct) : null;
    }
    private async Task SaveProviderDraftAsync(NexusUser? user, CancellationToken ct)
    {
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
        if (DownloadFolder != savedDownloadFolder)
        {
            await Services.Providers.SetDirectoriesAsync([DownloadFolder.Trim()], ct);
            savedDownloadFolder = Services.Providers.State.Directories[0]; DownloadFolder = savedDownloadFolder;
        }
    }
    private void ResetProviderDraft()
    { NexusApiKey = ""; RemoveNexusKey = HasSavedNexusKey; DownloadFolder = DownloadStore.DefaultDownloadsDirectory; }
}
