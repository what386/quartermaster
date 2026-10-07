using System.Net.Http.Json;
using System.Text.Json;

namespace Quartermaster.Providers.Clients.NexusMods;

public sealed partial class NexusClient
{
    private long? gameId;
    private async Task<JsonDocument> GraphAsync(string query, object variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.nexusmods.com/v2/graphql")
        { Content = JsonContent.Create(new { query, variables }) };
        using var response = await SendAsync(request, ct);
        var result = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (result.RootElement.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
        { result.Dispose(); throw new InvalidDataException("Nexus metadata request failed."); }
        return result;
    }
    private async Task<long> GameIdAsync(CancellationToken ct)
    {
        if (gameId is { } cached) return cached;
        using var data = await GraphAsync("query($domain: String!) { game(domainName: $domain) { id } }", new { domain = NexusLink.Game }, ct);
        gameId = Positive(Id(data.RootElement.GetProperty("data").GetProperty("game").GetProperty("id")));
        return gameId.Value;
    }
    private static long Id(JsonElement value) => value.ValueKind == JsonValueKind.String
        ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt64();
    public async Task<IReadOnlyList<ModRequirement>> GetRequirementsAsync(long modId, CancellationToken ct = default, long? fileId = null)
    {
        var game = await GameIdAsync(ct);
        var requirements = new List<ModRequirement>();
        const int pageSize = 100;
        for (var offset = 0; ; offset += pageSize)
        {
            const string query = "query($mod: ID!, $game: ID!, $offset: Int!) { mod(modId: $mod, gameId: $game) { legacyModRequirementsEnabled modRequirements { nexusRequirements(offset: $offset, count: 100) { totalCount nodes { modName modId gameId url notes externalRequirement } } } } }";
            using var data = await GraphAsync(query, new { mod = Positive(modId).ToString(), game = game.ToString(), offset }, ct);
            var mod = data.RootElement.GetProperty("data").GetProperty("mod");
            if (mod.TryGetProperty("legacyModRequirementsEnabled", out var legacy) && !legacy.GetBoolean())
                return await GetFileRequirementsAsync(modId, fileId, ct);
            var page = mod.GetProperty("modRequirements").GetProperty("nexusRequirements");
            var nodes = page.GetProperty("nodes");
            foreach (var node in nodes.EnumerateArray())
            {
                var installable = !node.GetProperty("externalRequirement").GetBoolean() && Id(node.GetProperty("gameId")) == game;
                var name = node.GetProperty("modName").GetString()!;
                var url = installable ? new Uri($"https://www.nexusmods.com/{NexusLink.Game}/mods/{Positive(Id(node.GetProperty("modId")))}")
                    : Uri.TryCreate(node.GetProperty("url").GetString(), UriKind.Absolute, out var external) && external is { Scheme: "https" or "http", UserInfo: "" }
                        ? external : throw new InvalidDataException($"Nexus returned an invalid requirement link for {name}.");
                requirements.Add(new(name, url, node.TryGetProperty("notes", out var notes) ? notes.GetString() : null, installable));
            }
            if (offset + nodes.GetArrayLength() >= page.GetProperty("totalCount").GetInt32()) break;
            if (nodes.GetArrayLength() == 0) throw new InvalidDataException("Nexus returned an incomplete requirement list.");
        }
        return requirements;
    }
    private async Task<Dictionary<string, string>> SearchScanResultsAsync(JsonElement nodes, CancellationToken ct)
    {
        var mods = nodes.EnumerateArray().Where(node => node.TryGetProperty("gameId", out var game) && game.ValueKind != JsonValueKind.Null).ToArray();
        var result = new Dictionary<string, string>();
        if (mods.Length == 0) return result;
        var fields = mods.Select((mod, index) => $"m{index}: modFiles(modId: \"{Id(mod.GetProperty("modId"))}\", gameId: \"{Id(mod.GetProperty("gameId"))}\") {{ categoryId detectedFileExtension scannedV2 }}");
        try
        {
            using var data = await GraphAsync("query { " + string.Join(" ", fields) + " }", new { }, ct);
            for (var index = 0; index < mods.Length; index++)
            {
                var statuses = data.RootElement.GetProperty("data").GetProperty($"m{index}").EnumerateArray()
                    .Where(file => file.GetProperty("categoryId").GetInt32() is 1 or 2 or 3 or 5 &&
                        file.GetProperty("detectedFileExtension").GetString()?.Equals("zip", StringComparison.OrdinalIgnoreCase) == true)
                    .Select(file => file.GetProperty("scannedV2").GetString() ?? "UNKNOWN").ToArray();
                result[Id(mods[index].GetProperty("modId")).ToString()] = statuses.OrderBy(ScanRank).FirstOrDefault() ?? "UNKNOWN";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { /* Scan metadata must not prevent browsing search results; missing results display Unknown. */ }
        return result;
    }
    private static int ScanRank(string status) => status switch
    {
        "QUARANTINED" => 0,
        "VERIFIED" => 4,
        "INTERNALLY_VERIFIED" or "MANUALLY_VERIFIED" => 3,
        "QUEUED" or "WAITING_REPORT" => 2,
        _ => 1
    };
}
