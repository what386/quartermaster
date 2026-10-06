namespace Quartermaster.Providers.Clients.AyakaMods;

// XenForo envelopes use snake_case. The public API is still in development;
// version strings and game names accept both of XenForo's usual field names.
internal sealed record AyakaPagination(int CurrentPage, int LastPage, int PerPage, int Total);
internal sealed record AyakaGames(AyakaGame[] Games, AyakaPagination? Pagination);
internal sealed record AyakaGame(long GameId, string? Title, string? Name)
{
    public string DisplayName => Title ?? Name ?? "";
}
internal sealed record AyakaMod(long ModId, string Title, string? TagLine, long GameId,
    string ModType, string? ViewUrl, string? Version, string? IconUrl);
internal sealed record AyakaModsPage(AyakaMod[] Mods, AyakaPagination? Pagination);
internal sealed record AyakaModResponse(AyakaMod Mod);
internal sealed record AyakaVersions(AyakaVersion[] Versions, AyakaPagination? Pagination);
internal sealed record AyakaVersion(long VersionId, long ModId, string? VersionString,
    string? Version, AyakaFile[]? Files)
{
    public string? DisplayVersion => VersionString ?? Version;
}
internal sealed record AyakaVersionResponse(AyakaVersion Version);
internal sealed record AyakaFile(long Id, string Filename, long Size);
internal sealed record AyakaErrors(AyakaError[] Errors);
internal sealed record AyakaError(string Code, string Message);
