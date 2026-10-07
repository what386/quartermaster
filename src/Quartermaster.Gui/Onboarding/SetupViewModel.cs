using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Gui.Onboarding;

public enum SetupOutcome { SkipTour, TakeTour }

public sealed class SetupViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices services;
    private readonly CancellationTokenSource cancellation;
    private int step;
    private bool busy;
    private string error = "";
    public event Action<SetupOutcome>? Completed;
    public int Step => step;
    public bool IsGame => step == 0;
    public bool IsUpdates => step == 1;
    public bool IsNexus => step == 2;
    public bool IsGitHub => step == 3;
    public bool IsDownloads => step == 4;
    public bool IsReady => step == 5;
    public bool CanGoBack => step > 0;
    public bool CanSkipStep => IsReady || IsNexus || IsGitHub || IsGame && string.IsNullOrWhiteSpace(services.Session.GameDirectory);
    public bool CanSkipSetup => !IsReady && CanSkipStep;
    public bool IsBusy { get => busy; private set => Set(ref busy, value); }
    public string Error { get => error; private set => Set(ref error, value); }
    public string Progress => IsReady ? "SETUP COMPLETE" : $"QUICK SETUP · {step + 1} / 5";
    public string Title => step switch
    {
        0 => CanSkipStep ? "Find your game" : "Confirm your game folder",
        1 => "Keep Quartermaster current",
        2 => "Connect Nexus Mods",
        3 => "Connect GitHub",
        4 => "Watch your downloads",
        _ => "Ready to roll"
    };
    public string Description => step switch
    {
        0 => CanSkipStep ? "Choose Helldivers 2's installation or data folder. You can add mods now and set this before deploying."
            : "Confirm Helldivers 2's folder below, or choose the correct installation or data folder.",
        1 => "Check for app updates at startup. You'll be asked before anything is installed.",
        2 => "Optional. A personal API key enables Nexus search and downloads.",
        3 => "Optional. Public GitHub downloads work without a token; adding one raises API rate limits.",
        4 => "Choose where your browser saves mod ZIPs. Quartermaster picks them up when you start a download.",
        _ => "Your loadout starts here. Take a quick look around, or jump straight in."
    };
    public string NextLabel => IsReady ? "Take the tour" : "Continue";
    public string SkipLabel => IsReady ? "Skip tour" : "Skip this";
    public string GamePath { get; set; }
    public string DownloadFolder { get; set; }
    public bool AllowAutomaticUpdate { get; set; }
    public string NexusApiKey { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string NexusStatus { get; private set; } = "";
    public string GitHubStatus { get; private set; } = "";
    public AsyncCommand NextCommand { get; }
    public Command BackCommand { get; }
    public Command SkipCommand { get; }
    public Command SkipSetupCommand { get; }
    public AsyncCommand BrowseCommand { get; }
    public Command OpenNexusCommand { get; }
    public Command OpenGitHubCommand { get; }

    public SetupViewModel(AppServices services, CancellationToken ct)
    {
        this.services = services;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, services.Lifetime);
        GamePath = services.Session.GameDirectory;
        DownloadFolder = services.Providers.State.Directories.FirstOrDefault() ?? DownloadStore.DefaultDownloadsDirectory;
        AllowAutomaticUpdate = services.Session.Settings.AllowAutomaticUpdate;
        NextCommand = new(ContinueAsync, () => !IsBusy, ReportError);
        BackCommand = new(() => Move(step - 1), () => !IsBusy && CanGoBack);
        SkipCommand = new(() => { if (IsReady) Finish(SetupOutcome.SkipTour); else Move(step + 1); }, () => !IsBusy && CanSkipStep);
        SkipSetupCommand = new(() => Finish(SetupOutcome.TakeTour), () => !IsBusy && CanSkipSetup);
        BrowseCommand = new(async () =>
        {
            var folder = await services.Dialogs.PickFolderAsync(IsGame ? "Choose Helldivers 2 folder" : "Choose browser download folder");
            if (folder is null) return;
            if (IsGame) { GamePath = folder; Notify(nameof(GamePath)); }
            else { DownloadFolder = folder; Notify(nameof(DownloadFolder)); }
        }, () => !IsBusy, ReportError);
        OpenNexusCommand = new(() => services.OpenBrowser(new("https://www.nexusmods.com/settings/api-keys")));
        OpenGitHubCommand = new(() => services.OpenBrowser(new("https://github.com/settings/personal-access-tokens")));
    }
    public async Task InitializeAsync()
    {
        NexusStatus = await services.Keys.GetAsync("nexusmods", cancellation.Token) is null ? "" : "A key is already saved.";
        GitHubStatus = await services.Keys.GetAsync("github", cancellation.Token) is null ? "" : "A token is already saved.";
        Notify(nameof(NexusStatus)); Notify(nameof(GitHubStatus));
    }
    private async Task ContinueAsync()
    {
        IsBusy = true; Error = ""; RefreshCommands();
        try
        {
            var ct = cancellation.Token;
            ct.ThrowIfCancellationRequested();
            if (IsReady) { Finish(SetupOutcome.TakeTour); return; }
            if (IsGame)
            {
                if (string.IsNullOrWhiteSpace(GamePath) && !CanSkipStep) throw new ArgumentException("Choose the correct Helldivers 2 folder.");
                if (!string.IsNullOrWhiteSpace(GamePath)) await services.Session.SetGameDirectoryAsync(GamePath.Trim(), ct);
            }
            if (IsUpdates) await services.Session.SetOnboardingPreferencesAsync(AllowAutomaticUpdate, null, ct);
            if (IsNexus && !string.IsNullOrWhiteSpace(NexusApiKey))
            {
                await services.ValidateNexusKeyAsync(NexusApiKey.Trim(), ct);
                await services.Keys.SetAsync("nexusmods", NexusApiKey.Trim(), ct);
                NexusApiKey = ""; NexusStatus = "Key saved."; Notify(nameof(NexusApiKey)); Notify(nameof(NexusStatus));
            }
            if (IsGitHub && !string.IsNullOrWhiteSpace(GitHubToken))
            {
                await services.GitHub.ValidateTokenAsync(GitHubToken.Trim(), ct);
                await services.Keys.SetAsync("github", GitHubToken.Trim(), ct);
                GitHubToken = ""; GitHubStatus = "Token saved."; Notify(nameof(GitHubToken)); Notify(nameof(GitHubStatus));
            }
            if (IsDownloads)
            {
                if (string.IsNullOrWhiteSpace(DownloadFolder) || !Path.IsPathFullyQualified(DownloadFolder.Trim())) throw new ArgumentException("Choose an absolute download folder path.");
                await services.Providers.SetDirectoriesAsync([DownloadFolder.Trim()], ct);
            }
            ct.ThrowIfCancellationRequested(); Move(step + 1);
        }
        finally { IsBusy = false; RefreshCommands(); }
    }
    private void ReportError(Exception ex) { if (ex is not OperationCanceledException) Error = ex.Message; }
    private void Move(int value)
    {
        step = Math.Clamp(value, 0, 5); Error = "";
        foreach (var property in new[] { nameof(Step), nameof(IsGame), nameof(IsUpdates), nameof(IsNexus), nameof(IsGitHub),
            nameof(IsDownloads), nameof(IsReady), nameof(CanGoBack), nameof(CanSkipStep), nameof(CanSkipSetup), nameof(Progress), nameof(Title), nameof(Description), nameof(NextLabel), nameof(SkipLabel) }) Notify(property);
        RefreshCommands();
    }
    private void RefreshCommands() { NextCommand.Refresh(); BackCommand.Refresh(); SkipCommand.Refresh(); SkipSetupCommand.Refresh(); BrowseCommand.Refresh(); }
    private void Finish(SetupOutcome outcome) => Completed?.Invoke(outcome);
    public void Cancel() { cancellation.Cancel(); Finish(SetupOutcome.SkipTour); }
    public void Dispose() { cancellation.Cancel(); cancellation.Dispose(); }
}
