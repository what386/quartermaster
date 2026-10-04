using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Library.Mods;
using Quartermaster.Library.Profiles;
using Quartermaster.Library.Storage;
using Xunit;

namespace Quartermaster.Library.Tests;

public class DeploymentTests
{
    [Fact]
    public async Task DeployRedeployAndPurgePreserveOriginalsAndReplaceAllPatches()
    {
        using var f = new Fixture();
        var a = await f.Library.ImportAsync(f.Source("a", 1));
        var b = await f.Library.ImportAsync(f.Source("b", 2));
        var other = await f.Library.ImportAsync(f.Source("other", 3, "0123456789abcdef"));
        var profile = ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Add(ProfileEditor.Create("Default"), a), b), other);
        await f.Library.SaveProfileAsync(profile, true);
        var foreign = Path.Combine(f.Game, Fixture.Archive + ".patch_0.stream");
        var deployment = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents)));
        var state = await f.Library.LoadAsync();
        var plan = DeploymentPlanner.Create(ProfilePatches.Resolve(state, profile.Id));
        Assert.Equal(new[] { 0, 1, 0 }, plan.Patches.Select(p => p.Slot));
        var ledger = await deployment.DeployAsync(state, profile.Id, f.Game);
        Assert.Equal(6, ledger.Files.Count);
        Assert.Equal(Fixture.Patch(2), File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_1")));
        var health = await deployment.InspectAsync(f.Game);
        Assert.All(health.Files, file => Assert.Equal(ManagedFileStatus.Present, file.Status));
        Assert.Equal(ModDeploymentStatus.Deployed, DeploymentTracking.ForMod(a.Id, plan, ledger, health.Files));
        profile = ProfileEditor.SetEnabled(profile, b.Id, false);
        await f.Library.SaveProfileAsync(profile);
        ledger = await deployment.DeployAsync(await f.Library.LoadAsync(), profile.Id, f.Game);
        Assert.Equal(4, ledger.Files.Count); Assert.False(File.Exists(Path.Combine(f.Game, Fixture.Archive + ".patch_1")));
        File.WriteAllBytes(foreign, [0xfe]);
        await deployment.PurgeAsync(f.Game);
        Assert.False(File.Exists(foreign));
        Assert.Empty((await deployment.InspectAsync(f.Game)).Ledger.Files);
        Assert.Equal(Fixture.Patch(1), File.ReadAllBytes(f.Contents.GetFilePath(a.Id, a.PatchSets[0].Files[0])));
    }

    [Fact]
    public async Task ModifiedFilesRequirePurgeAndCanBeRemoved()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        var path = Path.Combine(f.Game, ledger.Files[0].Name); File.WriteAllBytes(path, [9]);
        Assert.Contains((await service.InspectAsync(f.Game)).Files, file => file.Status == ManagedFileStatus.Modified);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        await service.PurgeAsync(f.Game);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(Path.Combine(f.App, "deployment.lock")));
    }

    [Fact]
    public async Task MissingFilesRequirePurgeThenRedeploy()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        File.Delete(Path.Combine(f.Game, ledger.Files[0].Name));
        Assert.Contains((await service.InspectAsync(f.Game)).Files, file => file.Status == ManagedFileStatus.Missing);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        await service.PurgeAsync(f.Game);
        await service.DeployAsync(state, profile.Id, f.Game);
        Assert.All((await service.InspectAsync(f.Game)).Files, file => Assert.Equal(ManagedFileStatus.Present, file.Status));
    }

    [Fact]
    public async Task FailedPublicationLeavesIncompleteManifestAndNoBackups()
    {
        using var f = new Fixture(); var old = await f.Library.ImportAsync(f.Source("old", 1));
        var source = f.Source("new", 2, slot: 0);
        File.WriteAllBytes(Path.Combine(source, Fixture.Archive + ".patch_1"), Fixture.Patch(3));
        var newer = await f.Library.ImportAsync(source);
        var oldProfile = ProfileEditor.Add(ProfileEditor.Create("Old"), old); var newProfile = ProfileEditor.Add(ProfileEditor.Create("New"), newer);
        await f.Library.SaveProfileAsync(oldProfile); await f.Library.SaveProfileAsync(newProfile);
        var state = await f.Library.LoadAsync(); var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents)));
        var original = await service.DeployAsync(state, oldProfile.Id, f.Game);
        // A directory at the second new main destination forces failure after files were changed.
        Directory.CreateDirectory(Path.Combine(f.Game, Fixture.Archive + ".patch_1"));
        await Assert.ThrowsAnyAsync<IOException>(() => service.DeployAsync(state, newProfile.Id, f.Game));
        var incomplete = await service.InspectAsync(f.Game);
        Assert.Equal(DeploymentStatus.Deploying, incomplete.Ledger.Status);
        Assert.NotEqual(original.Signature, incomplete.Ledger.Signature);
        Assert.True(incomplete.NeedsPurge);
        Assert.False(Directory.Exists(Path.Combine(f.App, "deployments")));
        Directory.Delete(Path.Combine(f.Game, Fixture.Archive + ".patch_1"));
        await service.PurgeAsync(f.Game);
        await service.DeployAsync(state, newProfile.Id, f.Game);
        Assert.False((await service.InspectAsync(f.Game)).NeedsPurge);
        Assert.Empty(Directory.GetFiles(f.Game, "*.tmp"));
    }

    [Fact]
    public async Task TamperedLibraryFilesAndCancelledOperationsDoNotChangeDeployment()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents))); var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game);
        File.WriteAllBytes(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files[0]), [0]);
        await Assert.ThrowsAsync<IOException>(() => service.DeployAsync(state, profile.Id, f.Game));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PurgeAsync(f.Game, cancellation.Token));
        Assert.Equal(ledger.Signature, (await service.InspectAsync(f.Game)).Ledger.Signature);
        Assert.Equal(Fixture.Patch(), File.ReadAllBytes(Path.Combine(f.Game, Fixture.Archive + ".patch_0")));
    }

    [Fact]
    public async Task OptionalRepairUsesTemporaryStagingAndRecordsSourceHashes()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var profile = ProfileEditor.Add(ProfileEditor.Create("Test"), mod); await f.Library.SaveProfileAsync(profile);
        var service = new ProfileDeploymentService(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents), new FakeRepair()));
        var state = await f.Library.LoadAsync();
        var ledger = await service.DeployAsync(state, profile.Id, f.Game, new(Repatch: true));
        var main = ledger.Files.Single(file => file.Kind == PatchFileKind.Main);
        Assert.NotEqual(main.SourceSha256, main.Sha256);
        Assert.False(File.Exists(Path.Combine(f.App, "patches.json")));
        Assert.False(Directory.Exists(Path.Combine(f.App, "patched")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(f.App, "temp"), "deployment-*"));

        Assert.Equal(0xaa, File.ReadAllBytes(Path.Combine(f.Game, main.Name))[^1]);
        Assert.Equal(Fixture.Patch(), File.ReadAllBytes(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files[0])));
        var health = await service.InspectAsync(f.Game);
        Assert.Equal(ModDeploymentStatus.Deployed, DeploymentTracking.ForMod(mod.Id, DeploymentPlanner.Create(ProfilePatches.Resolve(state, profile.Id)), ledger, health.Files));
        service = new(new DeploymentService(new FileDeploymentStorage(f.App, f.Contents), new FakeRepair(1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DeployAsync(state, profile.Id, f.Game, new(Repatch: true)));
        Assert.Equal(ledger.Signature, (await service.InspectAsync(f.Game)).Ledger.Signature);
    }

    [Fact]
    public async Task RepatchExportPreservesAllVariantsAndMetadataWithoutChangingLibrary()
    {
        using var f = new Fixture();
        var source = f.Source("Variants"); f.Source("Variants/alternate", 2);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "Original metadata");
        var mod = await f.Library.ImportAsync(source);
        var destination = Path.Combine(f.Root, "repatched.zip");
        await f.Contents.ExportRepatchedAsync(mod, destination, new FakeRepair());
        var exported = await f.Library.ImportAsync(destination);
        Assert.Equal(2, exported.PatchSets.Count);
        foreach (var patch in exported.PatchSets)
        {
            var main = patch.Files.Single(file => file.Kind == PatchFileKind.Main);
            Assert.Equal(0xaa, File.ReadAllBytes(f.Contents.GetFilePath(exported.Id, main))[^1]);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(f.Contents.GetFilePath(exported.Id, patch.Files.Single(file => file.Kind == PatchFileKind.Stream))));
        }
        Assert.Equal("Original metadata", File.ReadAllText(Path.Combine(f.Contents.GetModDirectory(exported.Id), "readme.txt")));
        Assert.Equal(Fixture.Patch(), File.ReadAllBytes(f.Contents.GetFilePath(mod.Id, mod.PatchSets.Single(p => p.Folder == "").Files.Single(file => file.Kind == PatchFileKind.Main))));
        Assert.Single(Directory.EnumerateFiles(f.Game)); Assert.False(File.Exists(Path.Combine(f.App, "deployment.lock")));
    }

    [Fact]
    public async Task FailedOrCancelledExportPreservesExistingDestinationAndRemovesTemporaryFiles()
    {
        using var f = new Fixture(); var mod = await f.Library.ImportAsync(f.Source("source"));
        var destination = Path.Combine(f.Root, "existing.zip"); await File.WriteAllBytesAsync(destination, [9, 8, 7]);
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Contents.ExportRepatchedAsync(mod, destination, new FakeRepair(1)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Contents.ExportRepatchedAsync(mod, destination, new FakeRepair(), cancellation.Token));
        File.Delete(f.Contents.GetFilePath(mod.Id, mod.PatchSets[0].Files.Single(file => file.Kind == PatchFileKind.Stream)));
        await Assert.ThrowsAsync<IOException>(() => f.Contents.ExportRepatchedAsync(mod, destination, new FakeRepair()));
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(f.Root, "*.tmp"));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Contents.ExportRepatchedAsync(mod, Path.Combine(f.Contents.GetModDirectory(mod.Id), "export.zip"), new FakeRepair()));
    }

    private sealed class FakeRepair(int removed = 0) : IPatchRepairer
    {
        public RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default)
        { var bytes = patch.ToArray(); bytes[^1] = 0xaa; return new(bytes, removed); }
    }
}
