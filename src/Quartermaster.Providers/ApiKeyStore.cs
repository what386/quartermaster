using Quartermaster.Library.Storage;

namespace Quartermaster.Providers;

/// <summary>Local credentials are separate from library metadata and profile exports.</summary>
public sealed class ApiKeyStore(string directory)
{
    private readonly string root = Path.Combine(Path.GetFullPath(directory), "credentials");
    private string PathFor(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider) || provider.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw new ArgumentException("Invalid provider ID.");
        if (Directory.Exists(root) && new DirectoryInfo(root).LinkTarget is not null) throw new IOException("Credential directory cannot be a link.");
        var path = Path.Combine(root, provider + ".key");
        if (File.Exists(path) && new FileInfo(path).LinkTarget is not null) throw new IOException("Credential file cannot be a link.");
        return path;
    }
    public async Task<string?> GetAsync(string provider, CancellationToken ct = default)
    {
        var path = PathFor(provider);
        return File.Exists(path) ? (await File.ReadAllTextAsync(path, ct)).Trim() : null;
    }
    public async Task SetAsync(string provider, string? key, CancellationToken ct = default)
    {
        var path = PathFor(provider);
        if (string.IsNullOrWhiteSpace(key)) { File.Delete(path); return; }
        key = key.Trim();
        if (key.Length > 4096 || key.Any(c => c < 33 || c > 126)) throw new ArgumentException("Invalid API key.");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = TemporaryStorage.PathFor(directory, "credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            await using (var writer = new StreamWriter(stream)) { await writer.WriteAsync(key.AsMemory(), ct); await writer.FlushAsync(ct); }
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
