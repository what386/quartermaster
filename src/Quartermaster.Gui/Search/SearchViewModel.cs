using Quartermaster.Gui.Services;
using Quartermaster.Gui.Shared;
using Quartermaster.Providers.Providers;

namespace Quartermaster.Gui.Search;

public sealed class SearchViewModel : SessionViewModel
{
    private string query = "";
    public string Query { get => query; set { if (Set(ref query, value)) SearchCommand.Refresh(); } }
    public IReadOnlyList<SearchModItem> Results { get; private set; } = [];
    public string ResultSummary { get; private set; } = "Search Nexus Mods for Helldivers 2 mods.";
    public AsyncCommand SearchCommand { get; }
    public SearchViewModel(AppServices services) : base(services)
    {
        SearchCommand = Operations.CreateCommand("Searching mods", async ct =>
        {
            var result = await Services.Providers.SearchAsync("nexusmods", Query, ct);
            Results = result.Select(item => new SearchModItem(item, Services)).ToArray();
            ResultSummary = result.Count == 0 ? "No matching mods." : $"{result.Count} results";
            Notify(nameof(Results)); Notify(nameof(ResultSummary));
        }, () => !string.IsNullOrWhiteSpace(Query));
        Operations.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(OperationState.IsBusy)) return;
            foreach (var item in Results) { item.AddCommand.Refresh(); item.OpenCommand.Refresh(); }
        };
    }
    protected override void Refresh() { }
}

public sealed class SearchModItem
{
    private readonly SearchResult result;
    public string Title => result.Name + (string.IsNullOrWhiteSpace(result.Version) ? "" : " · " + result.Version);
    public string Description => result.Summary;
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
