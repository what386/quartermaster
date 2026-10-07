using System.Security.Cryptography;
using Quartermaster.Library.Mods;
using Quartermaster.Providers.Downloads;

namespace Quartermaster.Providers.Clients.NexusMods;

public sealed class NexusAdapter(NexusClient client) : IModProvider
{
    public const string ProviderId = "nexusmods";
    public string Id => ProviderId;
    public string DisplayName => "Nexus Mods";
    public bool CanHandle(Uri link) => link.IsAbsoluteUri &&
        (link.Scheme == "https" && link.Host is "www.nexusmods.com" or "nexusmods.com" || link.Scheme == "nxm" && link.Host == NexusLink.Game);
    public bool IsDownloadLink(Uri link) => CanHandle(link) && link.Scheme == "nxm";
    public async Task<ProviderMod> ResolveAsync(string value, CancellationToken ct = default)
    {
        var link = NexusLink.Parse(value);
        var mod = await client.GetModAsync(link.ModId, ct);
        if (!mod.Available) throw new InvalidOperationException("This Nexus mod is unavailable.");
        var files = await client.GetFilesAsync(link.ModId, ct);
        var available = files.Files.Where(file => file.IsAvailable && (link.FileId is null || file.Id == link.FileId)).Select(file => Map(link.ModId, file)).ToArray();
        if (available.Length == 0) throw new InvalidOperationException("This mod has no available files.");
        return new(link.ModId.ToString(), mod.Name, mod.Summary, mod.Version, link.Page, available);
    }
    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int offset = 0, CancellationToken ct = default) => client.SearchAsync(query, offset, ct);
    public Task<IReadOnlyList<ModRequirement>> GetRequirementsAsync(string link, CancellationToken ct = default)
    {
        var parsed = NexusLink.Parse(link);
        return client.GetRequirementsAsync(parsed.ModId, ct, parsed.FileId);
    }
    public async Task<ProviderUpdate> CheckUpdateAsync(SourceReference source, CancellationToken ct = default)
    {
        if (source.Provider != Id || !long.TryParse(source.ModId, out var modId) || modId <= 0) throw new ArgumentException("Invalid Nexus source.");
        if (!long.TryParse(source.FileId, out var installed) || installed <= 0) return new(UpdateStatus.Unknown, Reason: "Choose the installed Nexus file to track its update chain.");
        var files = await client.GetFilesAsync(modId, ct);
        var reachable = new HashSet<long> { installed };
        bool changed;
        do
        {
            changed = false;
            foreach (var edge in files.Updates)
                if (reachable.Contains(edge.OldId)) changed |= reachable.Add(edge.NewId);
        } while (changed);
        var candidates = files.Files.Where(file => file.Id != installed && file.IsAvailable && reachable.Contains(file.Id) &&
            files.Updates.All(edge => edge.OldId != file.Id || !reachable.Contains(edge.NewId))).ToArray();
        if (candidates.Length == 1) return new(UpdateStatus.Available, Map(modId, candidates[0]));
        if (candidates.Length > 1) return new(UpdateStatus.Unknown, Reason: "The update chain has multiple replacements. Choose a file on Nexus.");
        if (reachable.Count > 1) return new(UpdateStatus.Unknown, Reason: "The replacement file is unavailable or its update chain is invalid.");
        return files.Files.Any(file => file.Id == installed && file.IsAvailable) ? new(UpdateStatus.Current) :
            new(UpdateStatus.Unknown, Reason: "The installed file is archived or missing and has no linked replacement.");
    }
    public DownloadScanner CreateScanner() => new NexusDownloadScanner(client);
    public Task DownloadAsync(string link, ProviderFile file, string destination, CancellationToken ct = default)
    {
        var parsed = NexusLink.Parse(link);
        if (file.Provider != Id || parsed.ModId.ToString() != file.ModId || !long.TryParse(file.FileId, out var id)) throw new ArgumentException("Download belongs to another Nexus mod.");
        return client.DownloadAsync(parsed, id, destination, ct);
    }
    private static ProviderFile Map(long modId, NexusFile file) => new(ProviderId, modId.ToString(), file.Id.ToString(), file.Name, file.FileName, file.Version,
        new Uri($"https://www.nexusmods.com/{NexusLink.Game}/mods/{modId}?tab=files&file_id={file.Id}"), file.IsPrimary, file.Size);
}

public sealed class NexusDownloadScanner(NexusClient client, TimeSpan? pollInterval = null) : DownloadScanner(pollInterval)
{
    // Nexus may change archive filenames. Verify any ZIP using Nexus's hash lookup, not its filename.
    protected override bool IsCandidate(ProviderFile expected, FileInfo file) => file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
        (expected.Size is null || expected.Size == file.Length);
    protected override async Task<bool> VerifyAsync(ProviderFile expected, string path, CancellationToken ct)
    {
        if (expected.Sha256 is not null || expected.Md5 is not null) return await base.VerifyAsync(expected, path, ct);
        return await client.MatchesHashAsync(long.Parse(expected.ModId), long.Parse(expected.FileId), await HashAsync(path, HashAlgorithmName.MD5, ct), ct);
    }
}
