using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Repatcher;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Core.Patching;

public sealed class RepatcherAdapter(IUnitResourceSource resources, RepairOptions? options = null) : IPatchRepairer
{
    private readonly global::Quartermaster.Repatcher.Repatcher engine = new(resources, options);
    public RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default)
    {
        var result = engine.RepairPatch(patch, cancellationToken);
        if (result.Status is RepairStatus.Corrupted or RepairStatus.Failed) throw new InvalidDataException(result.Error);
        return new(result.Data!, result.RemovedUnits);
    }

    public RepairedPatchSet RepairSet(PatchSetData patch, CancellationToken cancellationToken = default)
    {
        var result = engine.RepairSet(patch, cancellationToken);
        if (result.Output is null) throw new InvalidDataException(result.Error);
        return new(result.Output, result.RemovedUnits) { Warnings = result.Warnings.Select(w => w.Message).ToArray() };
    }
}
