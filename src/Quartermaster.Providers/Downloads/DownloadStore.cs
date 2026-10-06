using System.Text.Json;
using Quartermaster.Library.Storage;
using Quartermaster.Providers.Clients;

namespace Quartermaster.Providers.Downloads;

public enum DownloadStatus { Waiting, Downloading, Importing, Complete, Failed, Cancelled, NeedsConfirmation }
public sealed record DownloadJob(Guid Id, ProviderFile File, DownloadStatus Status = DownloadStatus.Waiting,
    Guid? ProfileId = null, Guid? ReplacesModId = null, string? Error = null)
{
    public IReadOnlyList<DownloadFingerprint> ExistingFiles { get; init; } = [];
    public string? Warning { get; init; }
    public DownloadFingerprint? ConfirmationFile { get; init; }
}
public sealed record DownloadFingerprint(string Path, long Size, DateTime LastWrite);
public sealed record DownloadState(IReadOnlyList<string> Directories, IReadOnlyList<DownloadJob> Jobs);

public sealed class DownloadStore(string directory)
{
    private readonly string path = Path.Combine(directory, "downloads.json");
    private static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public async Task<DownloadState> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new([DefaultDownloadsDirectory], []);
        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<DownloadState>(stream, json, ct) ?? throw new InvalidDataException("Empty download queue.");
        if (state.Directories is null || state.Jobs is null || state.Directories.Count == 0 ||
            state.Directories.Any(folder => string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) || state.Jobs.Any(job => job is null || job.File is null || job.File.DownloadPage is null) || state.Jobs.Select(job => job.Id).Distinct().Count() != state.Jobs.Count ||
            state.Jobs.Any(job => job.Id == Guid.Empty || !Enum.IsDefined(job.Status) || (!job.File.DownloadPage.IsAbsoluteUri || job.File.DownloadPage.Scheme != "https" || job.File.DownloadPage.UserInfo != "")))
            throw new InvalidDataException("Invalid download queue.");
        return state;
    }
    public async Task SaveAsync(DownloadState state, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var temporary = TemporaryStorage.PathFor(directory, "downloads-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(stream, state, json, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string DefaultDownloadsDirectory
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsLinux())
            {
                var config = Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "user-dirs.dirs");
                if (File.Exists(config))
                {
                    var line = File.ReadLines(config).FirstOrDefault(value => value.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal));
                    var folder = line?["XDG_DOWNLOAD_DIR=".Length..].Trim().Trim('"').Replace("$HOME", home, StringComparison.Ordinal);
                    if (folder is not null && Path.IsPathFullyQualified(folder)) return folder;
                }
            }
            return Path.Combine(home, "Downloads");
        }
    }
}
