using System.Diagnostics;
using System.Text;

namespace Quartermaster.SelfUpdate;

internal static class ZsyncDownload
{
    public static string? FindExecutable() => FindExecutable(Environment.GetEnvironmentVariable("PATH"));

    internal static string? FindExecutable(string? searchPath)
    {
        if (!OperatingSystem.IsLinux()) return null;
        // Prefer the curl-based implementation; both tools accept the arguments below.
        foreach (var name in new[] { "zsync2", "zsync" })
            foreach (var directory in (searchPath ?? "").Split(Path.PathSeparator))
            {
                if (!Path.IsPathFullyQualified(directory)) continue;
                var path = Path.Combine(directory, name);
                try
                {
                    if (File.Exists(path) && (File.GetUnixFileMode(path) &
                        (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                        return path;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        return null;
    }

    public static async Task RunAsync(string executable, string seed, string control, Uri controlUrl,
        string output, long expectedSize, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(output)!,
        };
        foreach (var argument in new[] { "-i", seed, "-o", output, "-u", controlUrl.AbsoluteUri, control })
            info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start()) throw new IOException("Could not start the zsync update tool.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException("Could not start the zsync update tool.", ex);
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var exited = process.WaitForExitAsync(cancel.Token);
            while (!exited.IsCompleted)
            {
                if (File.Exists(output) && new FileInfo(output).Length > expectedSize)
                    throw new InvalidDataException("The zsync output exceeds the advertised release size.");
                await Task.WhenAny(exited, Task.Delay(100, cancel.Token)).ConfigureAwait(false);
                cancel.Token.ThrowIfCancellationRequested();
            }
            await exited.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException($"AppImage synchronization failed (zsync exit code {process.ExitCode}): {(error.Length > 0 ? error : text).Trim()}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new IOException("AppImage synchronization timed out.");
        }
        finally
        {
            // Wait for termination before the caller removes partial downloads.
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        // Drain both pipes even after the diagnostic limit, so the child cannot block.
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
        return text.ToString();
    }
}
