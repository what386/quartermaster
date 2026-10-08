using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Gui.Onboarding;

public enum SetupOutcome
{
    SkipTour,
    TakeTour,
}

public sealed class SetupViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices services;
    private readonly CancellationTokenSource cancellation;
    private int step;
    private bool busy;
    private string error = "";
    private string gamePath = "";
    private string nexusApiKey = "";
    private string gitHubToken = "";
    public event Action<SetupOutcome>? Completed;
    public int Step => step;
    public bool IsGame => step == 0;
    public bool IsUpdates => step == 1;
    public bool IsNexus => step == 2;
    public bool IsGitHub => step == 3;
    public bool IsDownloads => step == 4;
    public bool IsReady => step == 5;
    public bool CanGoBack => step > 0;
    public bool CanSkipStep =>
        IsReady
        || IsNexus
        || IsGitHub
        || IsGame && string.IsNullOrWhiteSpace(services.Session.GameDirectory);
    public bool CanSkipSetup => !IsReady && CanSkipStep;
    public bool IsBusy
    {
        get => busy;
        private set => Set(ref busy, value);
    }
    public string Error
    {
        get => error;
        private set => Set(ref error, value);
    }
    public string Progress => IsReady ? Localizer.Text("SETUP COMPLETE") : Localizer.Interpolate($"SETUP · {step + 1} / 5");
    public string Title =>
        step switch
        {
            0 => CanSkipStep ? Localizer.Text("Find your game") : Localizer.Text("Confirm your game folder"),
            1 => Localizer.Text("Keep Quartermaster current"),
            2 => Localizer.Text("Connect Nexus Mods"),
            3 => Localizer.Text("Connect GitHub"),
            4 => Localizer.Text("Watch your downloads"),
            _ => Localizer.Text("Ready to go"),
        };
    public string Description =>
        step switch
        {
            0 => CanSkipStep
                ? Localizer.Text("Choose Helldivers 2's installation or data folder. You can add mods now and set this before deploying.")
                : Localizer.Text("Confirm Helldivers 2's folder below, or choose the correct installation or data folder."),
            1 => Localizer.Text("Check for app updates at startup. You'll be asked before anything is installed."),
            2 => Localizer.Text("Optional. A personal API key enables Nexus search and downloads."),
            3 =>
                Localizer.Text("Optional. Public GitHub downloads work without a token; adding one raises API rate limits."),
            4 =>
                Localizer.Text("Choose where your browser saves mod ZIPs. Quartermaster picks them up when you start a download."),
            _ => Localizer.Text("Take a quick look around, or skip the tour."),
        };
    public string NextLabel =>
        IsReady ? Localizer.Text("Take the tour")
        : SkipsCurrentStep ? Localizer.Text("Skip")
        : Localizer.Text("Continue");
    private bool SkipsCurrentStep =>
        CanSkipStep
        && (
            IsGame && string.IsNullOrWhiteSpace(GamePath)
            || IsNexus && !RemoveNexusKey && string.IsNullOrWhiteSpace(NexusApiKey)
            || IsGitHub && !RemoveGitHubToken && string.IsNullOrWhiteSpace(GitHubToken)
        );
    public string GamePath
    {
        get => gamePath;
        set
        {
            if (Set(ref gamePath, value))
                Notify(nameof(NextLabel));
        }
    }
    public string DownloadFolder { get; set; }
    public bool AllowAutomaticUpdate { get; set; }
    public string NexusApiKey
    {
        get => nexusApiKey;
        set
        {
            if (Set(ref nexusApiKey, value))
                Notify(nameof(NextLabel));
        }
    }
    public string GitHubToken
    {
        get => gitHubToken;
        set
        {
            if (Set(ref gitHubToken, value))
                Notify(nameof(NextLabel));
        }
    }
    public bool HasSavedNexusKey { get; private set; }
    public bool HasSavedGitHubToken { get; private set; }
    public bool RemoveNexusKey { get; private set; }
    public bool RemoveGitHubToken { get; private set; }
    public string RemoveNexusKeyLabel => RemoveNexusKey ? Localizer.Text("Undo removal") : Localizer.Text("Remove stored secret");
    public string RemoveGitHubTokenLabel =>
        RemoveGitHubToken ? Localizer.Text("Undo removal") : Localizer.Text("Remove stored secret");
    public Command RemoveNexusKeyCommand { get; }
    public Command RemoveGitHubTokenCommand { get; }
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
        DownloadFolder =
            services.Providers.State.Directories.FirstOrDefault()
            ?? DownloadStore.DefaultDownloadsDirectory;
        AllowAutomaticUpdate = services.Session.Settings.AllowAutomaticUpdate;
        RemoveNexusKeyCommand = new(
            () =>
            {
                RemoveNexusKey = !RemoveNexusKey;
                NexusApiKey = RemoveNexusKey ? "" : StoredSecret.Mask;
                Notify(nameof(RemoveNexusKey));
                Notify(nameof(RemoveNexusKeyLabel));
                Notify(nameof(NextLabel));
                Notify(nameof(NexusApiKey));
            },
            () => !IsBusy && HasSavedNexusKey
        );
        RemoveGitHubTokenCommand = new(
            () =>
            {
                RemoveGitHubToken = !RemoveGitHubToken;
                GitHubToken = RemoveGitHubToken ? "" : StoredSecret.Mask;
                Notify(nameof(RemoveGitHubToken));
                Notify(nameof(RemoveGitHubTokenLabel));
                Notify(nameof(NextLabel));
                Notify(nameof(GitHubToken));
            },
            () => !IsBusy && HasSavedGitHubToken
        );
        NextCommand = new(ContinueAsync, () => !IsBusy, ReportError);
        BackCommand = new(() => Move(step - 1), () => !IsBusy && CanGoBack);
        SkipCommand = new(() => Finish(SetupOutcome.SkipTour), () => !IsBusy && IsReady);
        SkipSetupCommand = new(() => Finish(SetupOutcome.TakeTour), () => !IsBusy && CanSkipSetup);
        BrowseCommand = new(
            async () =>
            {
                var folder = await services.Dialogs.PickFolderAsync(
                    IsGame ? Localizer.Text("Choose Helldivers 2 folder") : Localizer.Text("Choose browser download folder")
                );
                if (folder is null)
                    return;
                if (IsGame)
                {
                    GamePath = folder;
                    Notify(nameof(GamePath));
                }
                else
                {
                    DownloadFolder = folder;
                    Notify(nameof(DownloadFolder));
                }
            },
            () => !IsBusy,
            ReportError
        );
        OpenNexusCommand = new(() =>
            services.OpenBrowser(new("https://www.nexusmods.com/settings/api-keys"))
        );
        OpenGitHubCommand = new(() =>
            services.OpenBrowser(new("https://github.com/settings/personal-access-tokens"))
        );
    }

    public async Task InitializeAsync()
    {
        HasSavedNexusKey = !string.IsNullOrWhiteSpace(
            await services.Keys.GetAsync("nexusmods", cancellation.Token)
        );
        HasSavedGitHubToken = !string.IsNullOrWhiteSpace(
            await services.Keys.GetAsync("github", cancellation.Token)
        );
        NexusApiKey = HasSavedNexusKey ? StoredSecret.Mask : "";
        GitHubToken = HasSavedGitHubToken ? StoredSecret.Mask : "";
        Notify(nameof(HasSavedNexusKey));
        Notify(nameof(HasSavedGitHubToken));
        Notify(nameof(NexusApiKey));
        Notify(nameof(GitHubToken));
        RefreshCommands();
    }

    private async Task ContinueAsync()
    {
        IsBusy = true;
        Error = "";
        RefreshCommands();
        try
        {
            var ct = cancellation.Token;
            ct.ThrowIfCancellationRequested();
            if (IsReady)
            {
                Finish(SetupOutcome.TakeTour);
                return;
            }
            if (IsGame)
            {
                if (string.IsNullOrWhiteSpace(GamePath) && !CanSkipStep)
                    throw new ArgumentException(Localizer.Text("Choose the correct Helldivers 2 folder."));
                if (!string.IsNullOrWhiteSpace(GamePath))
                    await services.Session.SetGameDirectoryAsync(GamePath.Trim(), ct);
            }
            if (IsUpdates)
                await services.Session.SetOnboardingPreferencesAsync(
                    AllowAutomaticUpdate,
                    null,
                    ct
                );
            if (IsNexus && RemoveNexusKey)
            {
                await services.Keys.SetAsync("nexusmods", null, ct);
                HasSavedNexusKey = false;
                RemoveNexusKey = false;
                Notify(nameof(HasSavedNexusKey));
                Notify(nameof(RemoveNexusKey));
                Notify(nameof(RemoveNexusKeyLabel));
            }
            else if (IsNexus && StoredSecret.HasReplacement(NexusApiKey))
            {
                await services.ValidateNexusKeyAsync(NexusApiKey.Trim(), ct);
                await services.Keys.SetAsync("nexusmods", NexusApiKey.Trim(), ct);
                NexusApiKey = StoredSecret.Mask;
                HasSavedNexusKey = true;
                Notify(nameof(NexusApiKey));
                Notify(nameof(HasSavedNexusKey));
            }
            if (IsGitHub && RemoveGitHubToken)
            {
                await services.Keys.SetAsync("github", null, ct);
                HasSavedGitHubToken = false;
                RemoveGitHubToken = false;
                Notify(nameof(HasSavedGitHubToken));
                Notify(nameof(RemoveGitHubToken));
                Notify(nameof(RemoveGitHubTokenLabel));
            }
            else if (IsGitHub && StoredSecret.HasReplacement(GitHubToken))
            {
                await services.GitHub.ValidateTokenAsync(GitHubToken.Trim(), ct);
                await services.Keys.SetAsync("github", GitHubToken.Trim(), ct);
                GitHubToken = StoredSecret.Mask;
                HasSavedGitHubToken = true;
                Notify(nameof(GitHubToken));
                Notify(nameof(HasSavedGitHubToken));
            }
            if (IsDownloads)
            {
                if (
                    string.IsNullOrWhiteSpace(DownloadFolder)
                    || !Path.IsPathFullyQualified(DownloadFolder.Trim())
                )
                    throw new ArgumentException(Localizer.Text("Choose an absolute download folder path."));
                await services.Providers.SetDirectoriesAsync([DownloadFolder.Trim()], ct);
            }
            ct.ThrowIfCancellationRequested();
            Move(step + 1);
        }
        finally
        {
            IsBusy = false;
            RefreshCommands();
        }
    }

    private void ReportError(Exception ex)
    {
        if (ex is not OperationCanceledException)
            Error = ex.Message;
    }

    private void Move(int value)
    {
        step = Math.Clamp(value, 0, 5);
        Error = "";
        foreach (
            var property in new[]
            {
                nameof(Step),
                nameof(IsGame),
                nameof(IsUpdates),
                nameof(IsNexus),
                nameof(IsGitHub),
                nameof(IsDownloads),
                nameof(IsReady),
                nameof(CanGoBack),
                nameof(CanSkipStep),
                nameof(CanSkipSetup),
                nameof(Progress),
                nameof(Title),
                nameof(Description),
                nameof(NextLabel),
            }
        )
            Notify(property);
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        NextCommand.Refresh();
        BackCommand.Refresh();
        SkipCommand.Refresh();
        SkipSetupCommand.Refresh();
        BrowseCommand.Refresh();
        RemoveNexusKeyCommand.Refresh();
        RemoveGitHubTokenCommand.Refresh();
    }

    private void Finish(SetupOutcome outcome) => Completed?.Invoke(outcome);

    public void Cancel()
    {
        cancellation.Cancel();
        Finish(SetupOutcome.SkipTour);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
