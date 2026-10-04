using System.Security.Cryptography;
using System.Diagnostics;
using Quartermaster.Providers.Providers;

namespace Quartermaster.Providers.Downloads;

/// <summary>Providers override candidate selection and verification; browser files are never moved or deleted.</summary>
public abstract class DownloadScanner(TimeSpan? pollInterval = null, long maxBytes = 8L * 1024 * 1024 * 1024)
{
    private readonly TimeSpan interval = pollInterval ?? TimeSpan.FromSeconds(1);

    /// <summary>Opens a public file download page in the user's default browser.</summary>
    public virtual void OpenDownloadPage(ProviderFile expected, Action<Uri>? launch = null)
    {
        var page = expected.DownloadPage;
        if (!page.IsAbsoluteUri || page.Scheme != "https" || page.UserInfo.Length != 0)
            throw new ArgumentException("Download pages must be public HTTPS links.");
        if (launch is not null) launch(page);
        else Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true });
    }

    public async Task WaitForDownloadAsync(ProviderFile expected, Func<IReadOnlyList<string>> directories,
        string destination, CancellationToken ct = default)
    {
        if (interval <= TimeSpan.Zero || maxBytes <= 0) throw new ArgumentException("Invalid scanning limits.");
        var observed = new Dictionary<string, (long Size, DateTime LastWrite)>();
        var rejected = new HashSet<(string Path, long Size, DateTime LastWrite)>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var directory in directories().Distinct())
            {
                if (!Directory.Exists(directory)) continue;
                string[] paths;
                try { paths = Directory.GetFiles(directory); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                foreach (var path in paths)
                {
                    ct.ThrowIfCancellationRequested();
                    var info = new FileInfo(path);

                    (long Size, DateTime LastWrite) stamp;
                    try { if (info.LinkTarget is not null || IsPartial(info.Name) || !IsCandidate(expected, info)) continue; stamp = (info.Length, info.LastWriteTimeUtc); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    if (stamp.Size == 0 || stamp.Size > maxBytes || rejected.Contains((path, stamp.Size, stamp.LastWrite))) continue;
                    var stable = observed.TryGetValue(path, out var previous) && previous == stamp;
                    observed[path] = stamp;
                    if (!stable) continue;
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
                    var openingSource = true;
                    try
                    {
                        // On Windows deny concurrent writers. On other platforms compare the source again after copying.
                        await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            openingSource = false;
                            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                            var buffer = new byte[81920]; long size = 0; int count;
                            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                            {
                                size += count;
                                if (size > maxBytes || size > stamp.Size) throw new DownloadChangingException();
                                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                            }
                            if (size != stamp.Size) continue;
                        }
                        info.Refresh();
                        if (!info.Exists || info.Length != stamp.Size || info.LastWriteTimeUtc != stamp.LastWrite) continue;
                        if (!await VerifyAsync(expected, temporary, ct).ConfigureAwait(false))
                        { rejected.Add((path, stamp.Size, stamp.LastWrite)); continue; }
                        ct.ThrowIfCancellationRequested();
                        File.Move(temporary, destination, overwrite: true);
                        return;
                    }
                    catch (IOException) when (openingSource) { observed.Remove(path); }
                    catch (UnauthorizedAccessException) when (openingSource) { observed.Remove(path); }
                    catch (DownloadChangingException) { observed.Remove(path); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }

    protected virtual bool IsCandidate(ProviderFile expected, FileInfo file)
    {
        if (expected.Size is { } size && file.Length != size) return false;
        if (expected.Sha256 is not null || expected.Md5 is not null) return true;
        var name = Path.GetFileNameWithoutExtension(file.Name);
        var suffix = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (suffix >= 0 && name.EndsWith(')') && int.TryParse(name.AsSpan(suffix + 2, name.Length - suffix - 3), out _)) name = name[..suffix];
        return (name + file.Extension).Equals(expected.FileName, StringComparison.OrdinalIgnoreCase);
    }

    protected virtual async Task<bool> VerifyAsync(ProviderFile expected, string path, CancellationToken ct)
    {
        if (expected.Size is { } size && new FileInfo(path).Length != size) return false;
        if (expected.Sha256 is { } sha) return sha.Equals(await HashAsync(path, HashAlgorithmName.SHA256, ct), StringComparison.OrdinalIgnoreCase);
        if (expected.Md5 is { } md5) return md5.Equals(await HashAsync(path, HashAlgorithmName.MD5, ct), StringComparison.OrdinalIgnoreCase);
        throw new InvalidOperationException("This provider must verify downloaded file identity; filenames alone are insufficient.");
    }

    protected static async Task<string> HashAsync(string path, HashAlgorithmName algorithm, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = new byte[81920]; int count;
        while ((count = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, count);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class DownloadChangingException() : IOException("Download is still changing.");

    private static bool IsPartial(string name) => new[] { ".part", ".crdownload", ".tmp", ".download" }
        .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}
