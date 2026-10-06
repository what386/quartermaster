using System.Globalization;
using System.Text.RegularExpressions;

namespace Quartermaster.Providers.Downloads;

public sealed record DownloadName(IReadOnlyList<string> Tokens, Version? Version)
{
    public string Identity => string.Concat(Tokens);
}
public sealed record DownloadNameMatch(Guid ModId, string Filename, double Score);

/// <summary>Tokenize on punctuation, separate versions, then rank by weighted token similarity.</summary>
public static class DownloadNames
{
    // Uses upstream-rs's filename approach: delimiter tokens, independent version
    // extraction and identity scoring. Platform tokens are kept because mod variants matter.
    private static readonly Regex VersionSuffix = new(@"(?ix)(?:^|[\s_.-])(?:v(?:ersion)?[\s_-]*|ver[\s_-]*)(?<version>\d+(?:[._-]\d+)*)(?:[a-z]\b)?|(?:^|[\s_.-])(?<version>\d+(?:[._]\d+)+)(?:[a-z]\b)?", RegexOptions.CultureInvariant);
    private static readonly Regex DateSuffix = new(@"(?<!\d)(?:19|20)\d{2}[-._]\d{2}[-._]\d{2}(?!\d)", RegexOptions.CultureInvariant);
    private static readonly Regex ImportedWrapper = new(@"^(?<title>.+?)\s+(?<id>\d+)\s+v?(?<version>\d+(?:[._-]\d+)*)\s+20\d{2}-\d{2}-\d{2}T\d{2}-\d{2}Z\s+[A-Za-z0-9]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex NexusWrapper = new(@"^(?<title>.+?)-(?<id>\d+)-(?<version>\d+(?:-\d+)*?)-\d{10,13}$", RegexOptions.CultureInvariant);
    private static readonly Regex CamelCase = new(@"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", RegexOptions.CultureInvariant);
    private static readonly Regex Tokens = new(@"[\p{L}\p{Nd}]+", RegexOptions.CultureInvariant);

    public static DownloadName Parse(string filename)
    {
        var name = DisplayName(filename);
        var wrapper = ImportedWrapper.Match(name);
        if (!wrapper.Success) wrapper = NexusWrapper.Match(name);
        Version? version = null;
        if (wrapper.Success)
        {
            version = ParseVersion(wrapper.Groups["version"].Value);
            name = wrapper.Groups["title"].Value + " nexus " + wrapper.Groups["id"].Value;
        }
        name = DateSuffix.Replace(name, "");
        var match = VersionSuffix.Match(name);
        if (match.Success) version ??= ParseVersion(match.Groups["version"].Value);
        name = VersionSuffix.Replace(name, "");
        return new(Tokens.Matches(CamelCase.Replace(name.Normalize(), " ")).Select(token => token.Value.ToLowerInvariant()).ToArray(), version);
    }

    private static Version? ParseVersion(string value)
    {
        var parts = value.Split(['.', '_', '-']);
        if (parts.Length is 0 or > 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return new(numbers[0], numbers[1], numbers[2], numbers[3]);
    }

    public static string Identity(string filename) => Parse(filename).Identity;
    public static string DisplayName(string filename) =>
        filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? filename[..^4] : filename;
    public static bool Matches(string installed, string downloaded)
    {
        return downloaded.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && Score(installed, downloaded) >= 0.85;
    }

    public static double Score(string installed, string downloaded)
    {
        var old = Parse(installed); var next = Parse(downloaded);
        if (old.Identity.Length <= 2 || next.Identity.Length <= 2) return 0;
        if (old.Identity == next.Identity) return 1;
        var left = old.Tokens.Distinct().ToList(); var right = next.Tokens.Distinct().ToList();
        // Wrapped provider IDs and game identifiers are identity, not release numbers.
        var oldId = old.Tokens.ToList().IndexOf("nexus"); var nextId = next.Tokens.ToList().IndexOf("nexus");
        if (oldId >= 0 && nextId >= 0 && oldId + 1 < old.Tokens.Count && nextId + 1 < next.Tokens.Count &&
            old.Tokens[oldId + 1] != next.Tokens[nextId + 1]) return 0;
        var total = left.Sum(Weight) + right.Sum(Weight);
        double matched = 0;
        foreach (var token in left.ToArray())
        {
            var best = right.Select(other => (Token: other, Score: TokenSimilarity(token, other)))
                .OrderByDescending(pair => pair.Score).FirstOrDefault();
            if (best.Score < 0.8) continue;
            matched += (Weight(token) + Weight(best.Token)) * best.Score;
            left.Remove(token); right.Remove(best.Token);
        }
        var score = matched / total;
        // Different named variants need confirmation even when a long common title dominates.
        if (left.Any(token => !Numeric(token)) && right.Any(token => !Numeric(token))) score = Math.Min(score, 0.84);
        return score;
    }

    private static bool Numeric(string token) => token.All(char.IsDigit);
    private static double Weight(string token) => Numeric(token) ? 0.25 : 1;

    private static double TokenSimilarity(string left, string right)
    {
        if (left == right) return 1;
        // Do not fuzzy-match embedded numbers (HD1 versus HD2), or treat bare numbers as versions.
        if (left.Any(char.IsDigit) || right.Any(char.IsDigit) || Math.Min(left.Length, right.Length) < 4) return 0;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return 1.0 - (double)previous[right.Length] / Math.Max(left.Length, right.Length);
    }

    public static IReadOnlyList<DownloadNameMatch> Rank(string downloaded, IEnumerable<(Guid Id, string Filename)> mods) =>
        mods.Select(mod => new DownloadNameMatch(mod.Id, mod.Filename, Score(mod.Filename, downloaded)))
            .OrderByDescending(match => match.Score).ThenBy(match => match.Filename, StringComparer.OrdinalIgnoreCase).ToArray();
}
