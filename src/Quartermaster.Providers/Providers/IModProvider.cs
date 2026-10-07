using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers.Clients;

public sealed record ProviderFile(string Provider, string ModId, string FileId, string Name, string FileName,
    string? Version, Uri DownloadPage, bool IsPrimary = false, long? Size = null, string? Sha256 = null, string? Md5 = null)
{
    public IReadOnlyList<ModDependency>? Dependencies { get; init; }
    public Uri? PageLink { get; init; }
}
public sealed record ProviderMod(string ModId, string Name, string Summary, string? Version, Uri Page, IReadOnlyList<ProviderFile> Files)
{
    public bool DownloadsDirectly { get; init; }
    public string DownloadAction => DownloadsDirectly ? "Download" : "Open download page";
    public string DownloadExplanation => DownloadsDirectly
        ? "Quartermaster downloads the ZIP and adds it to your library automatically. You can track or cancel it in Downloads."
        : "This opens the file's download page in your browser. Finish the download there; Quartermaster watches your configured download folder, verifies the ZIP, and adds it automatically. You can keep using the app while it waits.";
}
public sealed record SearchResult(string ModId, string Name, string Summary, string Version, Uri Page, Uri? Thumbnail = null)
{
    public string? VirusScanStatus { get; init; }
}
public sealed record ModRequirement(string Name, Uri Page, string? Notes = null, bool CanInstall = true)
{
    public IReadOnlyList<string>? AllowedFileIds { get; init; }
    public IReadOnlyList<ModRequirement> Alternatives { get; init; } = [];
    public ModDependency ToDependency() => new(Name, Page.AbsoluteUri, Notes, CanInstall)
    { AllowedFileIds = AllowedFileIds, Alternatives = Alternatives.Select(alternative => alternative.ToDependency()).ToArray() };
}
public enum UpdateStatus { Current, Available, Unknown }
public sealed record ProviderUpdate(UpdateStatus Status, ProviderFile? File = null, string? Reason = null);

public interface IModProvider
{
    string Id { get; }
    string DisplayName => Id;
    bool SupportsSearch => true;
    bool SupportsUpdateChecks => true;
    bool DownloadsDirectly => false;
    bool CanHandle(Uri link);
    bool IsDownloadLink(Uri link) => false;
    Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default);
    Task<IReadOnlyList<ModRequirement>> GetRequirementsAsync(string link, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModRequirement>>([]);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default);
    Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default);
    DownloadScanner CreateScanner();
    Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default);
}
