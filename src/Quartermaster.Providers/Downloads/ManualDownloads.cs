using Quartermaster.Library.Mods;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Providers.Downloads;

/// <summary>Manual page requests share the download queue, but never claim API-verified provider identities.</summary>
internal sealed class ManualDownloads : IModProvider
{
    public const string ProviderId = "manual";
    public string Id => ProviderId;
    public string DisplayName => "Manual update";
    public bool SupportsSearch => false;
    public bool SupportsUpdateChecks => false;
    public bool CanHandle(Uri link) => false;
    public Task<ProviderMod> ResolveAsync(string link, CancellationToken ct = default) => throw new NotSupportedException("Set the page link on an imported mod first.");
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default) => throw new NotSupportedException();
    public Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default) => throw new NotSupportedException("Download this mod in your browser.");
    public DownloadScanner CreateScanner() => new ManualDownloadScanner([], []);

    public static IReadOnlyList<DownloadFingerprint> Snapshot(IReadOnlyList<string> directories)
    {
        var result = new List<DownloadFingerprint>();
        foreach (var folder in directories.Distinct())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var path in Directory.GetFiles(folder))
                {
                    var file = new FileInfo(path);
                    if (file.Exists) result.Add(new(file.FullName, file.Length, file.LastWriteTimeUtc));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}

internal sealed class ManualDownloadScanner(IReadOnlyList<DownloadFingerprint> existing,
    IReadOnlyList<(Guid Id, string Filename, DateTimeOffset ImportedAt)> candidates) : DownloadScanner
{
    private FileInfo? lowerVersion;
    protected override bool IsCandidate(ProviderFile expected, FileInfo file)
    {
        if (!file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) || existing.Any(stamp => stamp.Path == file.FullName
            && stamp.Size == file.Length && stamp.LastWrite == file.LastWriteTimeUtc)) return false;
        var ranked = DownloadNames.Rank(file.Name, candidates.Select(mod => (mod.Id, mod.Filename)));
        var target = ranked.FirstOrDefault(match => match.ModId.ToString() == expected.ModId);
        if (target is null || target.Score < 0.7 || ranked[0].Score > target.Score) return false;
        if (ranked.Count(match => match.Score >= target.Score - 0.05) > 1)
            throw new InvalidOperationException($"Multiple library mods could match {file.Name}. Use Attach ZIP to choose the update for this mod.");
        var old = DownloadNames.Parse(expected.FileName); var next = DownloadNames.Parse(file.Name);
        if (target.Score < 0.85)
            throw new InvalidOperationException($"{file.Name} resembles {expected.FileName}, but the names differ. Use Attach ZIP to confirm this update.");
        lowerVersion = old.Version is not null && next.Version is not null && next.Version < old.Version ? file : null;
        if (old.Version is not null && next.Version is not null) return true;
        var installed = candidates.Single(mod => mod.Id == target.ModId);
        var baseline = existing.Where(stamp => Path.GetFileName(stamp.Path).Equals(expected.FileName, StringComparison.OrdinalIgnoreCase))
            .Select(stamp => stamp.LastWrite).DefaultIfEmpty(installed.ImportedAt.UtcDateTime).Max();
        return file.CreationTimeUtc > baseline || file.LastWriteTimeUtc > baseline;
    }
    protected override Task<bool> VerifyAsync(ProviderFile expected, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (lowerVersion is { } file)
            throw new ManualUpdateWarning(new(file.FullName, file.Length, file.LastWriteTimeUtc),
                $"{file.Name} appears to have a lower version than {expected.FileName}. The author may have changed the version format.");
        // This is explicitly a filename-based manual request, not provider hash verification.
        // ZIP structure and mod contents are validated by the library importer.
        return Task.FromResult(true);
    }
}

internal sealed class ManualUpdateWarning(DownloadFingerprint file, string message) : Exception(message)
{
    public DownloadFingerprint File { get; } = file;
}
