namespace Quartermaster.Repatcher;

public enum MissingUnitPolicy { Keep, Drop, Fail }
public enum CullingPolicy { Patch, CurrentGameArms }
public enum LodPolicy { EquipmentSafe, Preserve, CurrentGame }

public sealed record RepairOptions(MissingUnitPolicy MissingUnits = MissingUnitPolicy.Keep,
    LodPolicy Lod = LodPolicy.EquipmentSafe, CullingPolicy Culling = CullingPolicy.Patch,
    bool RecoverLegacy = true);

public sealed record RepairWarning(string Code, string Message, ulong? UnitId = null);
public sealed record UnitRepairOutcome(ulong UnitId, string Outcome, bool LodPreserved = false,
    bool CullingUpdated = false);
public sealed record RuntimeIdentityAudit(int CheckedUnits, int MissingUnits, int ChangedHeaders,
    int ChangedBoneReferences, int ChangedSidecarPayloads);
public sealed record ArmorCoreAudit(string Name, int PresentRoles, int VisualRoles, IReadOnlyList<string> MissingRoles);
