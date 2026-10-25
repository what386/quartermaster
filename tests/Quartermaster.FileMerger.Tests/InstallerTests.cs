using System.Text.Json.Nodes;
using Xunit;

namespace Quartermaster.FileMerger.Tests;

public sealed class InstallerTests
{
    [Fact]
    public async Task AppliesReordersRemovesAndRestoresWithoutAccumulatingEdits()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State);
        var path = Path.Combine(f.Game, Fixture.Target);
        var a = f.Mod("Water", 0, 200); var b = f.Mod("Vaulting", 4, 300);
        var first = await installer.PrepareAsync(f.Game, [a, b]);
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
        await installer.ApplyAsync(first);
        var installed = await File.ReadAllBytesAsync(path); Assert.NotEqual(f.Stock, installed);
        var second = await installer.PrepareAsync(f.Game, [b]); await installer.ApplyAsync(second);
        var expected = ConfigMerger.Merge(Fixture.Target, f.Stock, [b]);
        Assert.Equal(GameConfigCodec.Decrypt(expected.Content), GameConfigCodec.Decrypt(await File.ReadAllBytesAsync(path)));
        await installer.RestoreAsync(f.Game);
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [a]));
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, []));
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StalePlansAndExternalEditsAreRejectedBeforeAnyGameWrites()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State);
        var target2 = "data/game/generated_stratagem_settings.dl_bin";
        var other = Path.Combine(f.Game, target2); await File.WriteAllBytesAsync(other, f.Stock);
        var path = Path.Combine(f.Game, Fixture.Target);
        var plan = await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200), f.Mod("Stratagems", 4, 300, target2)]);
        await File.WriteAllBytesAsync(other, [1, 2, 3]);
        await Assert.ThrowsAsync<IOException>(() => installer.ApplyAsync(plan));
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(other));
        await File.WriteAllBytesAsync(other, f.Stock);
        await installer.ApplyAsync(plan);
        await Assert.ThrowsAsync<IOException>(() => installer.ApplyAsync(plan));
        await File.WriteAllBytesAsync(other, [4, 5, 6]);
        var current = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<IOException>(() => installer.RestoreAsync(f.Game));
        Assert.Equal(current, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task GameUpdatesAreNeverOverwrittenByOldBackupsAndNewModsCanAdoptNewStock()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State); var path = Path.Combine(f.Game, Fixture.Target);
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200)]));
        var updatedPlain = Fixture.Config(firstValue: 900); var updatedStock = GameConfigCodec.Encrypt(updatedPlain);
        await File.WriteAllBytesAsync(path, updatedStock);
        await Assert.ThrowsAsync<IOException>(() => installer.RestoreAsync(f.Game));
        await Assert.ThrowsAsync<IOException>(() => installer.PrepareAsync(f.Game, [f.Mod("Old water", 0, 200)]));
        var changed = updatedPlain.ToArray(); changed[32] ^= 1;
        var updatedMod = new FileMod("Updated water", Fixture.Target, GameConfigCodec.EncryptPreservingHash(changed, ConfigHash.Compute(updatedPlain)),
            "new.patch_0", [FileModPaths.Fingerprint(updatedStock)]);
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [updatedMod]));
        await installer.RestoreAsync(f.Game);
        Assert.Equal(updatedStock, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task CorruptBackupsAndCancellationDoNotChangeTheGame()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State); var path = Path.Combine(f.Game, Fixture.Target);
        var plan = await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200)]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => installer.ApplyAsync(plan, new CancellationToken(true)));
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
        await installer.ApplyAsync(plan);
        var installed = await File.ReadAllBytesAsync(path);
        var backup = Directory.GetFiles(f.State, "*.original", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(backup, [0]);
        await Assert.ThrowsAsync<IOException>(() => installer.RestoreAsync(f.Game));
        Assert.Equal(installed, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task PendingJournalRestoresEitherOldOrNewPublishedContent()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State); var path = Path.Combine(f.Game, Fixture.Target);
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200)]));
        var previous = await File.ReadAllBytesAsync(path);
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [f.Mod("Vaulting", 4, 300)]));
        var journal = Directory.GetFiles(f.State, "state.json", SearchOption.AllDirectories).Single();
        var state = JsonNode.Parse(await File.ReadAllTextAsync(journal))!; state["Complete"] = false;
        await File.WriteAllTextAsync(journal, state.ToJsonString());
        await File.WriteAllBytesAsync(path, previous); // crash before the new target was published
        await Assert.ThrowsAsync<IOException>(() => installer.PrepareAsync(f.Game, []));
        await installer.RestoreAsync(f.Game);
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task TargetCasingCannotCreateTwoWritesToTheSameFile()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State);
        var plan = await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200), f.Mod("Override", 0, 300, "data/game/generated_Entities.dl_bin")]);
        Assert.Equal(Fixture.Target, Assert.Single(plan.Files).Target);
        Assert.Equal("Override", Assert.Single(plan.Conflicts).WinningMod);
    }

    [Fact]
    public async Task LinkedStateRootResolvesBeforeCreatingBackupsAndSharesStateWithItsPhysicalPath()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture();
        var physical = Path.Combine(f.Root, "physical-state"); Directory.CreateDirectory(physical);
        var alias = Path.Combine(f.Root, "state-alias"); Directory.CreateSymbolicLink(alias, physical);
        var installer = new FileModInstaller(Path.Combine(alias, "filemods"));
        await installer.ApplyAsync(await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200)]));
        await new FileModInstaller(Path.Combine(physical, "filemods")).RestoreAsync(f.Game);
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(Path.Combine(f.Game, Fixture.Target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedGameRootsAndAncestorsShareStateAndCanRestore(bool ancestor)
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(); var installer = new FileModInstaller(f.State);
        var link = Path.Combine(f.Root, "game-alias");
        Directory.CreateSymbolicLink(link, ancestor ? f.Root : f.Game);
        var alias = ancestor ? Path.Combine(link, "game") : link;
        // Purging a linked installation with no file-mod journal is a valid no-op.
        await installer.RestoreAsync(alias);
        await installer.ApplyAsync(await installer.PrepareAsync(alias, [f.Mod("Water", 0, 200)]));
        Assert.True(Assert.Single((await installer.InspectAsync(f.Game)).Files).Matches);
        var plan = await installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 300)]);
        Assert.NotNull(plan.StateRevision);
        await installer.ApplyAsync(plan);
        await installer.RestoreAsync(alias);
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(Path.Combine(f.Game, Fixture.Target)));
        Assert.Single(Directory.GetDirectories(f.State));
    }

    [Fact]
    public async Task UnsafeTargetsAndLinksCannotRedirectWrites()
    {
        using var f = new Fixture(); var installer = new FileModInstaller(f.State);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.PrepareAsync(f.Game, [f.Mod("Unsafe", 0, 200, "../game.dll")]));
        if (OperatingSystem.IsWindows()) return;
        var outside = Path.Combine(f.Root, "external.dl_bin"); await File.WriteAllBytesAsync(outside, f.Stock);
        var path = Path.Combine(f.Game, Fixture.Target); File.Delete(path); File.CreateSymbolicLink(path, outside);
        await Assert.ThrowsAsync<IOException>(() => installer.PrepareAsync(f.Game, [f.Mod("Water", 0, 200)]));
        Assert.Equal(f.Stock, await File.ReadAllBytesAsync(outside));
        var link = Path.Combine(f.Root, "linked-game"); Directory.CreateSymbolicLink(link, f.Game);
        await Assert.ThrowsAsync<IOException>(() => installer.PrepareAsync(link, [f.Mod("Water", 0, 200)]));
    }
}
