using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers.Providers;

public sealed record ProviderFile(string Provider, string ModId, string FileId, string Name, string FileName,
    string? Version, Uri DownloadPage, bool IsPrimary = false, long? Size = null, string? Sha256 = null, string? Md5 = null);
public sealed record ProviderMod(string ModId, string Name, string Summary, string? Version, Uri Page, IReadOnlyList<ProviderFile> Files);
public sealed record SearchResult(string ModId, string Name, string Summary, string Version, Uri Page, Uri? Thumbnail = null);
public enum UpdateStatus { Current, Available, Unknown }
public sealed record ProviderUpdate(UpdateStatus Status, ProviderFile? File = null, string? Reason = null);

public interface IModProvider
{
    string Id { get; }
    string DisplayName => Id;
    bool CanHandle(Uri link);
    bool IsDownloadLink(Uri link) => false;
    Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default);
    Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default);
    Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default);
    DownloadScanner CreateScanner();
    Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default);
}
