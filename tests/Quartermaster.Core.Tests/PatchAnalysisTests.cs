using Quartermaster.Core.Deployment;
using Quartermaster.Core.Patching;
using Xunit;

namespace Quartermaster.Core.Tests;

public class PatchAnalysisTests
{
    private const string Archive = "9ba626afa44a3aa3";
    private static SelectedPatch Patch(Guid source, string archive = Archive, params ResourceKey[] resources) =>
        new(source, Guid.NewGuid(), archive, [new("content/main.patch", PatchFileKind.Main, 80, new string('a', 64))], resources);

    [Fact]
    public void PlansOrderedPatchesWithIndependentSlotsFromZero()
    {
        var a = Patch(Guid.NewGuid()); var b = Patch(Guid.NewGuid()); var other = Patch(a.SourceId, "0123456789abcdef");
        var request = new DeploymentRequest(Guid.NewGuid(), [a, other, b]);
        var plan = DeploymentPlanner.Create(request);
        Assert.Equal(new[] { 0, 0, 1 }, plan.Patches.Select(p => p.Slot));
        Assert.Equal(new[] { a.SourceId, other.SourceId, b.SourceId }, plan.Patches.Select(p => p.SourceId));
        Assert.Equal(request.SelectionId, plan.SelectionId);
        Assert.Equal(plan.Signature, DeploymentPlanner.Create(request).Signature);
        Assert.NotEqual(plan.Signature, DeploymentPlanner.Create(request with { Patches = [b, other, a] }).Signature);
        var changed = a with { Files = [a.Files[0] with { Sha256 = new string('b', 64) }] };
        Assert.NotEqual(plan.Signature, DeploymentPlanner.Create(request with { Patches = [changed, other, b] }).Signature);
    }

    [Fact]
    public void CollisionWinnerUsesInputOrderAndResourceIdentityIncludesType()
    {
        var resource = new ResourceKey(ulong.MaxValue, 123);
        var a = Patch(Guid.NewGuid(), Archive, resource);
        var b = Patch(Guid.NewGuid(), Archive.ToUpperInvariant(), resource);
        var c = Patch(Guid.NewGuid(), Archive, resource with { Type = 456 });
        var request = new DeploymentRequest(Guid.NewGuid(), [a, b, c]);
        var report = ConflictAnalyzer.Analyze(request);
        Assert.Equal(3, Assert.Single(report.Archives).SourceIds.Count);
        var collision = Assert.Single(report.Resources);
        Assert.Equal(resource, collision.Resource);
        Assert.Equal(b.SourceId, collision.WinningSourceId);
        Assert.Equal(a.SourceId, Assert.Single(ConflictAnalyzer.Analyze(request with { Patches = [c, b, a] }).Resources).WinningSourceId);
        Assert.Empty(ConflictAnalyzer.Analyze(request with { Patches = [a, c] }).Resources);
    }

    [Fact]
    public void InvalidAndDuplicateSelectionsAreRejectedByPlanningAndAnalysis()
    {
        var patch = Patch(Guid.NewGuid());
        var request = new DeploymentRequest(Guid.NewGuid(), [patch]);
        var invalid = new[]
        {
            request with { SelectionId = Guid.Empty },
            request with { Patches = [patch, patch] },
            request with { Patches = [patch with { SourceId = Guid.Empty }] },
            request with { Patches = [patch with { Archive = "invalid" }] },
            request with { Patches = [patch with { Files = [] }] },
            request with { Patches = [patch with { Files = [patch.Files[0], patch.Files[0]] }] }
        };
        foreach (var selection in invalid)
        {
            Assert.Throws<ArgumentException>(() => DeploymentPlanner.Create(selection));
            Assert.Throws<ArgumentException>(() => ConflictAnalyzer.Analyze(selection));
        }
    }

    [Theory]
    [InlineData("../file")]
    [InlineData("/absolute")]
    [InlineData("C:/file")]
    [InlineData("a\\b")]
    [InlineData("a//b")]
    public void UnsafeSourcePathsAreRejectedWithoutALibrary(string path)
    {
        var patch = Patch(Guid.NewGuid());
        var request = new DeploymentRequest(Guid.NewGuid(), [patch with { Files = [patch.Files[0] with { RelativePath = path }] }]);
        Assert.Throws<ArgumentException>(() => DeploymentPlanner.Create(request));
    }
}
