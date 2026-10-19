# Repatcher implementation

`Quartermaster.Repatcher` is the managed .NET engine for updating HELLDIVERS 2 mod
unit formats against installed game resources. It owns binary repair policies,
resource preservation, and staged file output. Library identity, deployment
selection, and deployment ledgers are owned by callers.

## Entry points and contracts

| API | Contract |
| --- | --- |
| `RepairSet(PatchSetData, CancellationToken)` | Complete in-memory update, including companion validation, legacy recovery, equipment diagnostics, optional culling, and output audits. |
| `RepairPatch(ReadOnlyMemory<byte>, CancellationToken)` | Strict main-buffer update. Does not validate companions or run recovery/classification. Throws if current-game culling is requested. |
| `RepairFileAsync(source, destination, CancellationToken)` | Reads a main patch and companions, runs complete repair, and publishes to new staging paths. |
| `RepairFolderAsync(source, staging, progress, CancellationToken)` | Processes discovered main patches sequentially in deterministic order, preserving relative paths and reporting per-file failures. |

`PatchSetData` contains `Main`, optional `Gpu`, and optional `Stream` buffers. A
null companion means absent; an empty buffer means a present empty file. Preserve
this distinction through adapters and serializers. Use explicit nullable branches
when converting arrays to `ReadOnlyMemory<byte>?`: a null array can otherwise
convert to a present, empty memory value.

`RepairSet` does not mutate the supplied buffers. `PatchDocument` exposes resource
views into its input, so callers must keep those buffers stable during processing.
Resource identity is `(FileId, TypeId)`; file IDs alone are insufficient.

```csharp
var engine = new Repatcher(resources, new RepairOptions(
    MissingUnits: MissingUnitPolicy.Keep,
    Lod: LodPolicy.EquipmentSafe,
    Culling: CullingPolicy.Patch,
    RecoverLegacy: true));

var result = engine.RepairSet(new PatchSetData(mainBytes, gpuBytes, streamBytes), ct);
if (result.Output is not { } output)
    throw new InvalidDataException(result.Error);

// Retain result.Warnings and use result.Changed to detect changes.
// Consume output.Main, output.Gpu, and output.Stream as one patch set.
```

## Processing flow

[`PatchSetRepairer.cs`](../src/Quartermaster.Repatcher/PatchSetRepairer.cs) orchestrates:

1. Optionally recover impossible main-resource offsets and establish the recovery
   baseline used for preservation checks.
2. Parse the complete patch set and classify equipment from canonical unit IDs
   and weighted resource-reference fingerprints.
3. Apply missing-unit policy and unit version/layout updates through `RepairMain`.
   Ordinary edits adjust resource sizes and subsequent main-data offsets while
   preserving unaffected bytes and companion layouts.
4. Optionally compose current-game culling for eligible arms. Keep the ordinary
   repair result available for fallback until the complete culling pass validates.
5. Rebuild recovered legacy sets by resource identity.
6. Audit retained resources and return output, diagnostics, and a byte/presence
   comparison against the original input.

## Repair policies

| Option | Default | Behavior |
| --- | --- | --- |
| `MissingUnits` | `Keep` | Preserve unavailable units with a warning. `Drop` removes their TOC rows; `Fail` prevents output. |
| `Lod` | `EquipmentSafe` | Preserve mod LODs and report uncertain classification. `Preserve` also preserves LODs but suppresses that warning. `CurrentGame` replaces LODs on version changes. |
| `Culling` | `Patch` | Preserve patch culling. `CurrentGameArms` enables selective composition using full installed unit payloads. |
| `RecoverLegacy` | `true` | Attempt unique-gap recovery before strict complete-set parsing. Does not affect `RepairPatch`. |

[`UnitRepair.cs`](../src/Quartermaster.Repatcher/Repair/UnitRepair.cs) reads the unit
version at `0x2c`, LOD start at `0x30`, and sixteen offset fields starting at `0x34`.
The first of those fields ends the LOD block. Matching versions return the mod
unit unchanged after header/range validation; LOD differences alone do not trigger
updates. Newer-than-installed unit versions are rejected.

When upgrading a unit older than `0xA4CD36`, vertex format values above 16 advance
by four. LOD replacement adjusts following offsets by the block-size delta. Under
preservation policies, the original LOD block is used while version/layout fields
are upgraded.

## Parsing, recovery, and serialization

[`PatchTable`](../src/Quartermaster.Repatcher/Formats/PatchTable.cs) validates the
little-endian header and type/resource counts. Resource rows are 80 bytes and
retain unknown metadata. [`PatchDocument`](../src/Quartermaster.Repatcher/Formats/PatchSetData.cs)
adds duplicate-key rejection, main-body overlap checks, and companion range checks.
Zero-length payloads do not require a readable buffer range. Companion payloads
may share backing ranges.

`PatchDocument.Rebuild` groups resources in type-table order, updates sizes and
offsets, assigns one-based entry indexes, and aligns GPU/stream payload starts to
64 bytes. Main output is padded to at least 256 bytes per resource. Header/type
metadata and resource payloads are preserved; physical ordering, padding, and
entry indexes may change. The generated triple is reparsed before return.

[`LegacyRecovery`](../src/Quartermaster.Repatcher/Repair/LegacyRecovery.cs) only
repairs impossible offsets when a single complete unclaimed gap exactly matches
the declared payload size. Assignments must also be distinct across damaged rows.
It does not infer physical order from TOC row order, truncate bodies, or repair
ambiguous/overlapping layouts. Successful recovery triggers a full set rebuild.

## Culling composition and preservation invariants

[`UnitCulling`](../src/Quartermaster.Repatcher/Repair/UnitCulling.cs) preserves raw
visible-mesh records while replacing the culling set. It imports required
transforms and their parents, remaps BoneInfo transform indexes, and appends
culling vertex/index bytes to the unit's original GPU payload. Rebuilt unit tables
and outer companion offsets must remain consistent.

Eligibility is limited to canonical slim/stocky left/right arm IDs from bundled
armor mappings. Other units retain patch culling. Composition requires
`IGameResourceSource`; recovery mode, missing eligibility, or an unavailable full
source produces a fallback warning. If any eligible arm fails composition, the
entire culling pass falls back to the ordinary repair result. Cancellation is not
converted into fallback.

Output audits require retained runtime headers (`0x00..0x27`), bone references
(`0x08..0x0f`), protected LODs, non-unit data, and stream payloads to match the
baseline. GPU payloads must match exactly except for composed arms, whose original
GPU bytes must remain an unchanged prefix. Explicitly dropped units are excluded
from retained-resource audits. Header/bone/sidecar audit failures return no output;
LOD and non-unit preservation failures are rejected as invalid data.

Repeated culling composition can append data and retain unused source streams.
Do not assume idempotence for that option or treat repacking alone as evidence
that a unit's visual payload changed.

## Results and error handling

`PatchSetRepair.Output` is null on a reported failure. `Updated` can include an
unchanged set; use `Changed`, which compares main bytes and companion bytes and
presence. `RepairedUnits` counts retained units whose main/GPU/stream payloads
changed, so a layout-only archive rebuild can change output without increasing
that count. `NoUnits` indicates the absence of unit resources.

`Units` records per-unit outcomes; `Equipment`, `ArmorAudits`, and `Audit` expose
classification and preservation diagnostics. `Warnings` contain stable code,
message, and optional unit ID fields. Codes include `MissingUnitKept`,
`MissingUnitDropped`, `UnclassifiedLodPreserved`, `CullingFallback`,
`LegacyOffsetRecovered`, and `LegacyRebuilt`.

Malformed input and checked-arithmetic failures become `Corrupted` results.
Runtime-identity audit failures return `Failed`. Invalid policy enum values throw
at construction; cancellation propagates. Callers must also handle filesystem
exceptions from installed-resource reads and file APIs. Folder repair collects
I/O/access failures per file and continues, so a batch may contain successful
staged sets alongside failures.

## Integration boundaries

[`RepatcherAdapter`](../src/Quartermaster.Core/Patching/RepatcherAdapter.cs) implements
`IPatchRepairer.RepairSet`, translates unsuccessful engine results to
`InvalidDataException`, and forwards warning messages. The interface's default
`RepairSet` implementation only wraps main-buffer repair and preserves companions;
implement it explicitly for engines that can rebuild them.

Deployment stages and verifies all original files before repair, writes the entire
returned set, and fingerprints the repaired bytes. `OwnedFile.SourceSha256`
retains source provenance. A new companion has `GeneratedByRepair = true` and is
associated with its source main patch; selection tracking, health checks, and
purge must account for these files. ZIP export uses the same complete-set contract
for every stored variant and carries metadata through separately.

`LibrarySession` runs preflight and deployment with the same repair options and
compares all three buffers when deciding whether output changed. Engine work and
archive indexing perform synchronous I/O/CPU work and should run off the UI thread.

File repair requires unused destination paths, stages companions and main output,
and publishes the main file last. Failures clean up files published by that
operation. Folder staging must be outside the source tree. Multi-file publication
is not a crash-proof transaction.

## Resource sources, maintenance, and tests

`IUnitResourceSource` supplies existence checks and current unit bodies.
`IGameResourceSource` adds full unit payloads and archive resource tables for
classification/culling. `GameArchives` supports legacy, DSAR, and bundled/slim
installations, caches resource tables per instance, and also exposes
`ReadResourcePayload` for non-unit resources. Concurrent engine calls require a
source supporting concurrent reads. Keep game assets stable during indexing and
repair; recreate the index after game updates. Buffers are limited to .NET array
sizes.

Equipment assets are embedded at build time. Refresh them with
`python3 scripts/refresh-repatcher-data.py`; see the
[data maintenance notes](../src/Quartermaster.Repatcher/Data/README.md) for revision
pinning, provenance, schema validation, and preview commands.

Run `dotnet test tests/Quartermaster.Repatcher.Tests` for engine changes. Changes to
companion handling, policy defaults, or publication also require library and GUI
integration checks; `dotnet test Quartermaster.slnx` covers those layers. Tests use
committed fixtures and synthetic resources without an installed game or ignored
example checkout. The [project README](../src/Quartermaster.Repatcher/README.md)
describes fixture regeneration tools. Add coverage for preservation failures and
fallback behavior when extending format support. Structural checks alone do not
establish in-game visual compatibility.
