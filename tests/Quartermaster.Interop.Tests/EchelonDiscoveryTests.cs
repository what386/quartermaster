using Quartermaster.Interop.Echelon;
using Xunit;

namespace Quartermaster.Interop.Tests;

public sealed class EchelonDiscoveryTests
{
    private static string Data(string path, string state = "{\"version\":2,\"library\":{},\"profiles\":[]}")
    {
        Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "state.json"), state); return Path.GetFullPath(path);
    }

    [Fact]
    public void FindsNativeWineAndExternalProtonLibrariesAndIgnoresInvalidState()
    {
        using var f = new Fixture();
        var appData = Path.Combine(f.Root, "appdata"); var home = Path.Combine(f.Root, "home");
        var native = Data(Path.Combine(appData, "Echelon"));
        var prefix = Path.Combine(f.Root, "prefix");
        var wine = Data(Path.Combine(prefix, "drive_c", "users", "user", "AppData", "Roaming", "Echelon"));
        Data(Path.Combine(prefix, "drive_c", "users", "broken", "AppData", "Roaming", "Echelon"), "not JSON");
        Data(Path.Combine(home, ".config", "Echelon"), "{\"version\":99,\"library\":{},\"profiles\":[]}");
        var steam = Path.Combine(f.Root, "steam"); var external = Path.Combine(f.Root, "external library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"), "\"libraryfolders\" { \"0\" { \"path\" \"" + external.Replace("\\", "\\\\") + "\" } }");
        var proton = Data(Path.Combine(external, "steamapps", "compatdata", "123", "pfx", "drive_c", "users", "steamuser", "AppData", "Roaming", "Echelon"));
        var found = EchelonDiscovery.FindDirectories(home, appData, [prefix, prefix, Path.Combine(f.Root, "missing")], [steam]);
        Assert.Equal(3, found.Count);
        Assert.Contains(native, found); Assert.Contains(wine, found); Assert.Contains(proton, found);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"version\":\"2\",\"library\":{},\"profiles\":[]}")]
    [InlineData("{\"version\":2,\"library\":[],\"profiles\":[]}")]
    [InlineData("{\"version\":2,\"library\":{}}")]
    public void DoesNotDetectUnrelatedOrInvalidStateFiles(string state)
    {
        using var f = new Fixture();
        Data(Path.Combine(f.Root, ".config", "Echelon"), state);
        Assert.Empty(EchelonDiscovery.FindDirectories(f.Root, Path.Combine(f.Root, "missing"), [], []));
    }

    [Fact]
    public void ResolvesPrefixAliasesWithoutOfferingTheSameLibraryTwice()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var prefix = Path.Combine(f.Root, "prefix");
        var folder = Data(Path.Combine(prefix, "drive_c", "users", "user", "AppData", "Roaming", "Echelon"));
        var alias = Path.Combine(f.Root, "alias"); Directory.CreateSymbolicLink(alias, prefix);
        Assert.Equal(folder, Assert.Single(EchelonDiscovery.FindDirectories(f.Root, Path.Combine(f.Root, "missing"), [prefix, alias], [])));
    }

    [Fact]
    public void CancellationStopsDiscovery()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => EchelonDiscovery.FindDirectories(f.Root, f.Root, [], [], cancellation.Token));
    }
}
