using Quartermaster.Core.Patching;
using Xunit;

namespace Quartermaster.Library.Tests;

public class DiscoveryTests
{
    [Fact]
    public void FindsSecondarySteamLibraryAndResolvesExplicitInstallPath()
    {
        using var f = new Fixture();
        var root = Path.Combine(f.Root, "steam"); Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        var secondary = Path.Combine(f.Root, "secondary"); var game = Path.Combine(secondary, "steamapps", "common", "Helldivers 2", "data");
        Directory.CreateDirectory(game); File.WriteAllBytes(Path.Combine(game, "bundles.nxa"), []);
        File.WriteAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"), $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{secondary.Replace("\\", "\\\\")}\" }} }}");
        var found = Assert.Single(SteamGameDiscovery.FindInstallations([root]));
        Assert.True(File.Exists(Path.Combine(found, "bundles.nxa")));
        Assert.Equal(found, SteamGameDiscovery.ResolveDataDirectory(Path.GetDirectoryName(game)!));
        Assert.Null(SteamGameDiscovery.ResolveDataDirectory(f.Root));
    }

    [Fact]
    public void SteamAliasesAreDeduplicated()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var root = Path.Combine(f.Root, "steam");
        var game = Path.Combine(root, "steamapps", "common", "Helldivers 2", "data");
        Directory.CreateDirectory(game); File.WriteAllBytes(Path.Combine(game, "bundles.nxa"), []);
        var alias = Path.Combine(f.Root, "alias"); Directory.CreateSymbolicLink(alias, root);
        Assert.Single(SteamGameDiscovery.FindInstallations([root, alias]));
        Assert.Equal(SteamGameDiscovery.ResolveDataDirectory(game), SteamGameDiscovery.ResolveDataDirectory(Path.Combine(alias, "steamapps", "common", "Helldivers 2")));
    }
}
