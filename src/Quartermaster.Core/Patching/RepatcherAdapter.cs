using Quartermaster.Core.Patching;
using Quartermaster.Core.Deployment;
using Quartermaster.Repatcher;
using Quartermaster.Repatcher.Archives;

namespace Quartermaster.Core.Patching;

public sealed class RepatcherAdapter(IUnitResourceSource resources) : IPatchRepairer
{
    private readonly global::Quartermaster.Repatcher.Repatcher engine = new(resources);
    public RepairedPatch Repair(ReadOnlyMemory<byte> patch, CancellationToken cancellationToken = default)
    {
        var result = engine.RepairPatch(patch, cancellationToken);
        if (result.Status is RepairStatus.Corrupted or RepairStatus.Failed) throw new InvalidDataException(result.Error);
        return new(result.Data!, result.RemovedUnits);
    }
}
