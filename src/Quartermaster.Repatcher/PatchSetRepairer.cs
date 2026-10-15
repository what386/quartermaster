using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;
using Quartermaster.Repatcher.Repair;

namespace Quartermaster.Repatcher;

public sealed partial class Repatcher
{
    /// <summary>Updates a complete patch set, validating all resource ranges before publication.</summary>
    public PatchSetRepair RepairSet(PatchSetData input, CancellationToken cancellationToken = default)
    {
        var warnings = new List<RepairWarning>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var main = options.RecoverLegacy ? LegacyRecovery.Recover(input.Main, warnings) : input.Main.ToArray();
            var recovered = !input.Main.Span.SequenceEqual(main);
            var baseline = input with { Main = main };
            var original = PatchDocument.Read(baseline);
            var safety = new EquipmentSafety(original, resources, cancellationToken);
            var preserveLod = options.Lod != LodPolicy.CurrentGame;
            if (options.Lod == LodPolicy.EquipmentSafe && safety.DetectedNames.Count == 0 &&
                original.Resources.Any(r => r.Record.TypeId == PatchTable.UnitTypeId))
                warnings.Add(new("UnclassifiedLodPreserved", "Equipment could not be classified; preserved custom LODs conservatively while updating supported format fields."));
            var repair = RepairMain(main, preserveLod, cancellationToken);
            warnings.AddRange(repair.Warnings);
            if (repair.Data is null) return new(repair.Status, null, Error: repair.Error) { Warnings = warnings, Units = repair.Units };
            var output = baseline with { Main = repair.Data };
            var updated = PatchDocument.Read(output);
            var entries = updated.Resources.ToArray();
            var replaced = new HashSet<ulong>();
            if (options.Culling == CullingPolicy.CurrentGameArms)
            {
                var eligible = entries.Where(r => r.Record.TypeId == PatchTable.UnitTypeId &&
                    EquipmentSafety.IsCanonicalArm(r.Record.FileId) && resources.ContainsUnit(r.Record.FileId)).ToArray();
                if (recovered || eligible.Length == 0 || resources is not IGameResourceSource)
                    warnings.Add(new("CullingFallback", recovered ? "Current-game culling is disabled for recovered legacy patches." :
                        eligible.Length == 0 ? "No canonical arm units are eligible for current-game culling; patch culling was preserved." :
                        "The game source cannot supply companion payloads; patch culling was preserved."));
                else
                {
                    try
                    {
                        var candidates = entries.ToArray();
                        var game = (IGameResourceSource)resources;
                        for (var i = 0; i < candidates.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!eligible.Any(e => e.Record.FileId == candidates[i].Record.FileId && candidates[i].Record.TypeId == PatchTable.UnitTypeId)) continue;
                            candidates[i] = UnitCulling.Replace(candidates[i], game.ReadUnitPayload(candidates[i].Record.FileId, cancellationToken));
                        }
                        // Publish this pass only if every eligible unit and the rebuilt triple validate.
                        var rebuilt = updated.Rebuild(candidates, cancellationToken);
                        entries = PatchDocument.Read(rebuilt).Resources.ToArray();
                        output = rebuilt;
                        replaced.UnionWith(eligible.Select(e => e.Record.FileId));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException or KeyNotFoundException)
                    { warnings.Add(new("CullingFallback", $"Current-game culling failed; preserved patch culling for all units. {ex.Message}")); }
                }
            }
            if (recovered)
            {
                output = updated.Rebuild(entries, cancellationToken);
                entries = PatchDocument.Read(output).Resources.ToArray();
                warnings.Add(new("LegacyRebuilt", "Rebuilt recovered TOC and companions by resource identity; verified payloads independently of old physical order."));
            }
            var byKey = entries.ToDictionary(r => (r.Record.FileId, r.Record.TypeId));
            var headers = 0; var bones = 0; var missing = 0; var sidecars = 0; var checkedUnits = 0;
            foreach (var before in original.Resources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unit = before.Record.TypeId == PatchTable.UnitTypeId;
                if (!byKey.TryGetValue((before.Record.FileId, before.Record.TypeId), out var after))
                {
                    if (unit && options.MissingUnits == MissingUnitPolicy.Drop) continue;
                    missing++;
                    continue;
                }
                if (unit)
                {
                    checkedUnits++;
                    if (!BinaryData.Slice(before.Data.Span, 0, 0x28).SequenceEqual(BinaryData.Slice(after.Data.Span, 0, 0x28))) headers++;
                    if (!BinaryData.Slice(before.Data.Span, 8, 8).SequenceEqual(BinaryData.Slice(after.Data.Span, 8, 8))) bones++;
                    if (preserveLod && !Lod(before.Data.Span).SequenceEqual(Lod(after.Data.Span)))
                        throw new InvalidDataException($"Unit {before.Record.FileId:x16} lost its protected LOD group.");
                }
                else if (!before.Data.Span.SequenceEqual(after.Data.Span))
                    throw new InvalidDataException($"Non-unit resource {before.Record.FileId:x16} changed during repair.");
                var gpuPreserved = replaced.Contains(before.Record.FileId) && unit
                    ? after.Gpu.Span.StartsWith(before.Gpu.Span) // Culling appends; the model's original buffers stay intact.
                    : before.Gpu.Span.SequenceEqual(after.Gpu.Span);
                if (!gpuPreserved || !before.Stream.Span.SequenceEqual(after.Stream.Span)) sidecars++;
            }
            var audit = new RuntimeIdentityAudit(checkedUnits, missing, headers, bones, sidecars);
            if (missing + headers + bones + sidecars > 0)
                return new(RepairStatus.Failed, null, Error: "Output failed runtime identity or companion payload audit.") { Warnings = warnings, Audit = audit };
            var outcomes = repair.Units.Select(u => replaced.Contains(u.UnitId) ? u with { CullingUpdated = true, Outcome = "Updated" } : u).ToArray();
            var repairedCount = original.Resources.Count(before => before.Record.TypeId == PatchTable.UnitTypeId &&
                byKey.TryGetValue((before.Record.FileId, before.Record.TypeId), out var after) &&
                (!before.Data.Span.SequenceEqual(after.Data.Span) || !before.Gpu.Span.SequenceEqual(after.Gpu.Span) || !before.Stream.Span.SequenceEqual(after.Stream.Span)));
            return new(repair.Status, output, repairedCount, repair.RemovedUnits)
            {
                Warnings = warnings, Units = outcomes, Equipment = safety.DetectedNames, ArmorAudits = safety.AuditArmor(PatchDocument.Read(output)),
                Audit = audit, Changed = !Same(input, output)
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or OverflowException)
        { return new(RepairStatus.Corrupted, null, Error: ex.Message) { Warnings = warnings }; }
    }

    private static ReadOnlySpan<byte> Lod(ReadOnlySpan<byte> data)
    {
        var start = BinaryData.U32(data, 0x30);
        return BinaryData.Slice(data, start, (long)BinaryData.U32(data, 0x34) - start);
    }

    private static bool Same(PatchSetData a, PatchSetData b) => a.Main.Span.SequenceEqual(b.Main.Span) &&
        Same(a.Gpu, b.Gpu) && Same(a.Stream, b.Stream);
    private static bool Same(ReadOnlyMemory<byte>? a, ReadOnlyMemory<byte>? b) => a.HasValue == b.HasValue &&
        (!a.HasValue || a.Value.Span.SequenceEqual(b!.Value.Span));
}
