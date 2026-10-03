using Quartermaster.Core.Deployment;
using Quartermaster.Library.Storage;
using Xunit;

namespace Quartermaster.Library.Tests;

public class RecoveryTests
{
    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99}")]
    public async Task MalformedManifestRequiresPurgeAndDoesNotPreventIt(string manifest)
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.App);
        var path = Path.Combine(f.App, "deployment.lock");
        await File.WriteAllTextAsync(path, manifest);
        var patch = Path.Combine(f.Game, Fixture.Archive + ".patch_0");
        File.WriteAllBytes(patch, Fixture.Patch());
        var sentinel = Path.Combine(f.Root, "outside"); File.WriteAllBytes(sentinel, [7]);
        var service = new DeploymentService(new FileDeploymentStorage(f.App, f.Contents));
        var inspection = await service.InspectAsync(f.Game);
        Assert.True(inspection.NeedsPurge); Assert.Equal(DeploymentStatus.Unknown, inspection.Ledger.Status);
        await service.PurgeAsync(f.Game);
        Assert.False(File.Exists(patch)); Assert.False(File.Exists(path));
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(sentinel));
        Assert.False((await service.InspectAsync(f.Game)).NeedsPurge);
    }

    [Fact]
    public async Task MissingManifestWithPatchesRequiresPurgeAndPreservesBaseArchives()
    {
        using var f = new Fixture();
        var basePath = Path.Combine(f.Game, Fixture.Archive); var before = File.ReadAllBytes(basePath);
        foreach (var suffix in new[] { "", ".stream", ".gpu_resources" })
            File.WriteAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_7" + suffix), [1]);
        var temporary = Path.Combine(f.Game, $".quartermaster-{Guid.NewGuid():N}-{Fixture.Archive}.patch_7.tmp");
        File.WriteAllBytes(temporary, [1]);
        var unrelated = Path.Combine(f.Game, "notes.patch_other"); File.WriteAllBytes(unrelated, [2]);
        var service = new DeploymentService(new FileDeploymentStorage(f.App, f.Contents));
        Assert.True((await service.InspectAsync(f.Game)).NeedsPurge);
        await service.PurgeAsync(f.Game);
        Assert.Empty(Directory.GetFiles(f.Game, "*.patch_7*"));
        Assert.False(File.Exists(temporary)); Assert.True(File.Exists(unrelated));
        Assert.Equal(before, File.ReadAllBytes(basePath));
    }

    [Fact]
    public async Task ManifestForAnotherGameDirectoryIsUnknown()
    {
        using var f = new Fixture(); Directory.CreateDirectory(f.App);
        await File.WriteAllTextAsync(Path.Combine(f.App, "deployment.lock"), """
            {"schemaVersion":1,"targetDirectory":"another-game","status":"complete","files":[]}
            """);
        var service = new DeploymentService(new FileDeploymentStorage(f.App, f.Contents));
        Assert.True((await service.InspectAsync(f.Game)).NeedsPurge);
        await service.PurgeAsync(f.Game);
    }
}
