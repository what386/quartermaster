namespace Quartermaster.Providers.Clients.GitHub;

public sealed record GitHubLink(string Repository, string? Tag = null, string? AssetName = null)
{
    public Uri Page => new("https://github.com/" + Repository);
    public static GitHubLink Parse(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host is not ("github.com" or "www.github.com") || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new ArgumentException("Enter a public GitHub repository or release link.");
        var parts = uri.AbsolutePath.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        bool Segment(string text) => text.Length > 0 && text is not ("." or "..") && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        if (parts.Length < 2 || !Segment(parts[0]) || !Segment(parts[1])) throw new ArgumentException("Invalid GitHub repository.");
        var repo = parts[1].EndsWith(".git", StringComparison.Ordinal) ? parts[1][..^4] : parts[1];
        if (!Segment(repo)) throw new ArgumentException("Invalid GitHub repository.");
        var repository = parts[0] + "/" + repo;
        if (parts.Length == 2 || parts.Length == 3 && parts[2] == "releases" ||
            parts.Length == 4 && parts[2] == "releases" && parts[3] == "latest") return new(repository);
        if (parts.Length >= 5 && parts[2] == "releases" && parts[4].Length > 0 && !parts[4].Any(char.IsControl))
        {
            if (parts.Length == 5 && parts[3] == "tag") return new(repository, parts[4]);
            if (parts.Length == 6 && parts[3] == "download" && parts[5].Length > 0 &&
                !parts[5].Any(c => char.IsControl(c) || c is '/' or '\\')) return new(repository, parts[4], parts[5]);
        }
        throw new ArgumentException("Use a GitHub repository, release, or release asset link.");
    }
}
