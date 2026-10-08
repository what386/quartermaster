using System.Text.Json;
using Quartermaster.Library.Mods;

namespace Quartermaster.Providers.Clients.NexusMods;

public sealed partial class NexusClient
{
    private async Task<JsonDocument> GetV3Async(string route, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.nexusmods.com/v3/" + route);
        using var response = await SendAsync(request, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<IReadOnlyList<ModRequirement>> GetFileRequirementsAsync(long modId, long? fileId, CancellationToken ct)
    {
        if (fileId is null)
        {
            var files = (await GetFilesAsync(modId, ct)).Files.Where(file => file.IsAvailable).ToArray();
            var selected = files.SingleOrDefault(file => file.IsPrimary) ?? (files.Length == 1 ? files[0] : null);
            fileId = selected?.Id ?? throw new InvalidOperationException("Choose a Nexus file to check its requirements.");
        }
        using var version = await GetV3Async($"games/{NexusLink.Game}/mod-file-versions/{Positive(fileId.Value)}", ct);
        var versionId = version.RootElement.GetProperty("data").GetProperty("id").GetString()
            ?? throw new InvalidDataException("Nexus returned no mod file version ID.");
        using var data = await GetV3Async($"mod-file-versions/{Uri.EscapeDataString(versionId)}/dependencies/ranges/materialized", ct);
        var result = new List<ModRequirement>();
        foreach (var dependency in data.RootElement.GetProperty("dependencies").EnumerateArray())
        {
            var alternatives = new List<ModRequirement>();
            var excluded = false;
            foreach (var candidate in dependency.GetProperty("candidate_mod_files").EnumerateArray())
            {
                var mod = candidate.GetProperty("mod");
                var domain = mod.GetProperty("game").GetProperty("domain_name").GetString()
                    ?? throw new InvalidDataException("Nexus returned no dependency game.");
                var target = Positive(Id(mod.GetProperty("game_scoped_id")));
                var page = new Uri($"https://www.nexusmods.com/{Uri.EscapeDataString(domain)}/mods/{target}");
                if (ModDependencyExclusions.IsExcluded(page)) { excluded = true; break; }
                var allowed = candidate.GetProperty("candidate_versions").EnumerateArray()
                    .Select(item => Positive(Id(item.GetProperty("game_scoped_id"))).ToString()).Distinct().ToArray();
                if (allowed.Length == 0) continue;
                alternatives.Add(new(mod.GetProperty("name").GetString()!,
                    page,
                    "Requires a compatible file version.", domain.Equals(NexusLink.Game, StringComparison.OrdinalIgnoreCase))
                    { AllowedFileIds = allowed });
            }
            if (excluded) continue;
            if (alternatives.Count == 0)
                throw new InvalidDataException("Nexus could not resolve a mod file requirement to an available version.");
            var first = alternatives.OrderByDescending(item => item.CanInstall).First();
            result.Add(first with { Alternatives = alternatives.Where(item => !ReferenceEquals(item, first)).ToArray() });
        }
        return result;
    }
}
