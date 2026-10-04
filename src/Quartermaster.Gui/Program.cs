using Avalonia;

namespace Quartermaster.Gui;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var directory = Services.AppServices.DefaultDataDirectory;
        Directory.CreateDirectory(directory);
        var links = args.Where(arg => arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase)).ToArray();
        var inbox = new Quartermaster.Providers.Protocol.NxmInbox(directory);
        foreach (var link in links) inbox.Enqueue(link);
        FileStream instance;
        try { instance = new(Path.Combine(directory, "app.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        using (instance) BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
