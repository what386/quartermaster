using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;

namespace Quartermaster.Gui.Onboarding;

public sealed class OnboardingCoordinator : ViewModelBase
{
    private sealed record Stop(PageKind Page, string Title, string Description);

    private static readonly Stop[] Stops =
    [
        new(
            PageKind.Mods,
            "Your mod library",
            "Add mods from a ZIP, folder, or link. Drag-and-drop works too."
        ),
        new(
            PageKind.Profiles,
            "Make a profile",
            "Use + to create profiles. Enable mods and arrange their order. Deploy applies the loadout; Run starts the game."
        ),
        new(
            PageKind.Search,
            "Search for mods",
            "Search Nexus Mods here. Open a download page to import a mod and its missing dependencies."
        ),
        new(
            PageKind.ManualChecks,
            "Check the stragglers",
            "Unsupported websites need you to check them. Open the linked page and download an update, or mark it as checked."
        ),
        new(
            PageKind.Downloads,
            "Track downloads",
            "Track imports here. Browser downloads are picked up from your chosen download folder."
        ),
        new(
            PageKind.Settings,
            "Change settings",
            "Change your game folder, keys, appearance, or update preference here."
        ),
    ];
    private readonly AppServices services;
    private readonly Action<PageKind> navigate;
    private readonly IReadOnlyList<NavigationItem> navigation;
    private bool running;
    private bool touring;
    private int stop;
    private TaskCompletionSource? tourCompletion;
    public bool IsRunning
    {
        get => running;
        private set => Set(ref running, value);
    }
    public bool IsTourVisible
    {
        get => touring;
        private set => Set(ref touring, value);
    }
    public bool IsProfileStep => IsTourVisible && Stops[stop].Page == PageKind.Profiles;
    public PageKind TourPage => Stops[stop].Page;
    public string Progress => $"QUICK TOUR · {stop + 1} / {Stops.Length}";
    public string Title => Stops[stop].Title;
    public string Description => Stops[stop].Description;
    public string NextLabel => stop == Stops.Length - 1 ? "Finish" : "Next";
    public bool CanGoBack => stop > 0;
    public Command NextCommand { get; }
    public Command BackCommand { get; }
    public Command SkipCommand { get; }
    public Func<Task>? RefreshSettingsAsync { get; set; }

    public OnboardingCoordinator(
        AppServices services,
        Action<PageKind> navigate,
        IReadOnlyList<NavigationItem> navigation
    )
    {
        this.services = services;
        this.navigate = navigate;
        this.navigation = navigation;
        NextCommand = new(
            () =>
            {
                if (stop == Stops.Length - 1)
                    FinishTour();
                else
                    ShowStop(stop + 1);
            },
            () => IsTourVisible && services.Operations.CanInteract
        );
        BackCommand = new(
            () => ShowStop(stop - 1),
            () => IsTourVisible && CanGoBack && services.Operations.CanInteract
        );
        SkipCommand = new(FinishTour, () => IsTourVisible);
        services.Operations.PropertyChanged += (_, _) => RefreshCommands();
    }

    public Task RunFirstRunAsync() =>
        services.Session.Settings.OnboardingCompleted ? Task.CompletedTask : RunAsync();

    public async Task RunAsync()
    {
        if (IsRunning || services.IsDisposed || !services.Operations.CanInteract)
            return;
        IsRunning = true;
        try
        {
            var outcome = SetupOutcome.SkipTour;
            var setupCompleted = false;
            await services.Operations.RunAsync(
                "Setting up Quartermaster",
                async ct =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                        ct,
                        services.Lifetime
                    );
                    using var model = new SetupViewModel(services, linked.Token);
                    await model.InitializeAsync();
                    outcome = await services
                        .Dialogs.ShowOnboardingAsync(model, linked.Token)
                        .WaitAsync(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    await services.Session.SetOnboardingPreferencesAsync(null, true, linked.Token);
                    setupCompleted = true;
                    if (RefreshSettingsAsync is not null)
                        await RefreshSettingsAsync();
                },
                showProgress: false
            );
            if (
                !setupCompleted
                || services.Operations.IsError
                || services.IsDisposed
                || outcome != SetupOutcome.TakeTour
            )
                return;
            tourCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IsTourVisible = true;
            ShowStop(0);
            await tourCompletion.Task.WaitAsync(services.Lifetime);
        }
        catch (OperationCanceledException) when (services.Lifetime.IsCancellationRequested) { }
        finally
        {
            FinishTour();
            IsRunning = false;
        }
    }

    private void ShowStop(int index)
    {
        stop = Math.Clamp(index, 0, Stops.Length - 1);
        navigate(Stops[stop].Page);
        foreach (var item in navigation)
            item.IsTourTarget = item.Page == Stops[stop].Page;
        foreach (
            var name in new[]
            {
                nameof(Progress),
                nameof(Title),
                nameof(Description),
                nameof(NextLabel),
                nameof(CanGoBack),
                nameof(IsProfileStep),
                nameof(TourPage),
            }
        )
            Notify(name);
        RefreshCommands();
    }

    private void FinishTour()
    {
        IsTourVisible = false;
        foreach (var item in navigation)
            item.IsTourTarget = false;
        Notify(nameof(IsProfileStep));
        RefreshCommands();
        tourCompletion?.TrySetResult();
    }

    private void RefreshCommands()
    {
        NextCommand.Refresh();
        BackCommand.Refresh();
        SkipCommand.Refresh();
    }
}
