using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Quartermaster.Providers.Providers.NexusMods;

namespace Quartermaster.Providers.Protocol;

/// <summary>Per-user registration and a private inbox for links forwarded to an already running application.</summary>
public static class NxmProtocol
{
    public static async Task RegisterAsync(CancellationToken ct = default)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate Quartermaster.");
        var arguments = new List<string> { executable };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments.Add(Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("Cannot locate the application assembly."));
        if (OperatingSystem.IsWindows()) { RegisterWindows(arguments); return; }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("nxm registration is currently supported on Linux and Windows.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var applications = Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share"), "applications");
        Directory.CreateDirectory(applications);
        await File.WriteAllTextAsync(Path.Combine(applications, "quartermaster.desktop"), CreateDesktopEntry(arguments), ct);
        var start = new ProcessStartInfo("xdg-mime") { UseShellExecute = false };
        foreach (var argument in new[] { "default", "quartermaster.desktop", "x-scheme-handler/nxm" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Cannot register nxm links.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0) throw new IOException("Could not make Quartermaster the default nxm handler.");
    }
    public static string CreateDesktopEntry(IReadOnlyList<string> command)
    {
        string Quote(string value)
        {
            if (value.Any(c => c is '\n' or '\r' or '\0')) throw new ArgumentException("Invalid executable path.");
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`").Replace("$", "\\$").Replace("%", "%%") + "\"";
        }
        return "[Desktop Entry]\nType=Application\nName=Quartermaster\nNoDisplay=true\nTerminal=false\nExec=" +
            string.Join(' ', command.Select(Quote)).Replace("\\", "\\\\") + " %u\nMimeType=x-scheme-handler/nxm;\n";
    }
    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(IReadOnlyList<string> command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\nxm");
        key.SetValue("", "URL:Quartermaster Nexus download"); key.SetValue("URL Protocol", "");
        using var open = key.CreateSubKey(@"shell\open\command");
        open.SetValue("", string.Join(' ', command.Select(value => "\"" + value.Replace("\"", "\\\"") + "\"")) + " \"%1\"");
    }
}

public sealed class NxmInbox(string directory)
{
    private readonly string inbox = Path.Combine(directory, "nxm-inbox");
    public void Enqueue(string link)
    {
        if (!NexusLink.Parse(link).IsNxm) throw new ArgumentException("Expected an nxm link.");
        Directory.CreateDirectory(inbox);
        if (new DirectoryInfo(inbox).LinkTarget is not null) throw new IOException("The nxm inbox cannot be a symbolic link.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(inbox, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(inbox, Guid.NewGuid().ToString("N") + ".tmp");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream)) writer.Write(link);
        File.Move(path, Path.ChangeExtension(path, ".nxm"));
    }
    public IReadOnlyList<string> Pending => Directory.Exists(inbox) ? Directory.GetFiles(inbox, "*.nxm") : [];
    public string Read(string path)
    {
        if (new FileInfo(path).LinkTarget is not null || new FileInfo(path).Length > 32768) throw new InvalidDataException("Invalid nxm inbox item.");
        var link = File.ReadAllText(path);
        if (!NexusLink.Parse(link).IsNxm) throw new InvalidDataException("Invalid nxm inbox item.");
        return link;
    }
    public void Remove(string path) => File.Delete(path);
}
