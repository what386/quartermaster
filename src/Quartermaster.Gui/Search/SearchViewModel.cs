using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Providers;

namespace Quartermaster.Gui.Search;

public sealed class SearchViewModel : SessionViewModel
{
    private string query = "";
    private string resultSummary = "Enter a mod name to search.";
    private SearchProvider? selectedProvider;
    public IReadOnlyList<SearchProvider> Providers { get; }
    public SearchProvider? SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (!Set(ref selectedProvider, value)) return;
            Results = []; Notify(nameof(Results));
            ResultSummary = "Enter a mod name to search.";
            SearchCommand.Refresh();
        }
    }
    public string Query { get => query; set { if (Set(ref query, value)) SearchCommand.Refresh(); } }
    public IReadOnlyList<SearchModItem> Results { get; private set; } = [];
    public string ResultSummary { get => resultSummary; private set => Set(ref resultSummary, value); }
    public AsyncCommand SearchCommand { get; }
    public SearchViewModel(AppServices services) : base(services)
    {
        Providers = services.Providers.AvailableProviders.Select(provider => new SearchProvider(provider.Id, provider.DisplayName)).ToArray();
        selectedProvider = Providers.FirstOrDefault(provider => provider.Id == "nexusmods") ?? Providers.FirstOrDefault();
        SearchCommand = Operations.CreateCommand("Searching mods", async ct =>
        {
            var provider = SelectedProvider!;
            Results = []; Notify(nameof(Results));
            ResultSummary = $"Searching {provider.Name}…";
            try
            {
                var result = await Services.Providers.SearchAsync(provider.Id, Query.Trim(), ct);
                Results = result.Select(item => new SearchModItem(item, Services)).ToArray();
                Notify(nameof(Results));
                ResultSummary = result.Count == 0 ? "No matching mods." : $"{result.Count} results";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { ResultSummary = "Search cancelled."; throw; }
            catch (Exception ex)
            { ResultSummary = $"Search failed: {ex.Message}"; throw; }
        }, () => SelectedProvider is not null && !string.IsNullOrWhiteSpace(Query));
        Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationState.IsBusy)) return;
            foreach (var item in Results) { item.AddCommand.Refresh(); item.OpenCommand.Refresh(); }
        };
    }
    protected override void Refresh() { }
}

public sealed record SearchProvider(string Id, string Name);

public sealed class SearchModItem
{
    private readonly SearchResult result;
    public string Title => result.Name + (string.IsNullOrWhiteSpace(result.Version) ? "" : " · " + result.Version);
    public string Description => result.Summary;
    public Uri? Thumbnail => result.Thumbnail;
    public AsyncCommand AddCommand { get; }
    public AsyncCommand OpenCommand { get; }
    public SearchModItem(SearchResult result, AppServices services)
    {
        this.result = result;
        AddCommand = new(() => services.Operations.RunAsync("Adding mod", ct => services.Downloads.AddLinkAsync(result.Page.AbsoluteUri, ct)),
            () => services.Operations.CanInteract, services.Operations.ReportError);
        OpenCommand = new(() => services.Operations.RunAsync("Opening mod page", _ =>
        { services.OpenBrowser(result.Page); return Task.CompletedTask; }), () => services.Operations.CanInteract, services.Operations.ReportError);
    }
}
