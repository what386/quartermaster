# Quartermaster.Repatcher

Native .NET 10 updates for HELLDIVERS 2 mods. The engine updates unit version and
layout fields against installed game data, preserves the mod's equipment identity
and appearance, and writes only to separate staging destinations. It does not
retarget cosmetics or scale models.

Runtime dependencies are managed .NET only; DSAR decoding uses
K4os.Compression.LZ4. Source and dependency attribution is recorded in
[THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt).

See the [implementation guide](../../docs/repatcher.md) for processing flow, binary
invariants, integration contracts, and maintenance.

## Usage

```csharp
using Quartermaster.Repatcher;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;

// Indexing performs synchronous disk I/O; run it off the UI thread.
var archives = await Task.Run(() => GameArchives.Open(gameDataDirectory, cancellationToken));
var repatcher = new Repatcher(archives); // Keep missing units and custom LODs.
var result = await repatcher.RepairFolderAsync(importedModDirectory, stagingDirectory,
    new Progress<FileRepair>(ReportRepair), cancellationToken);
if (result.HasErrors) { /* Inspect failures before deploying staged results. */ }
```

`RepairSet(PatchSetData)` validates the main patch and optional GPU/stream
companions, returns a `PatchSetRepair` with the complete output, changed flag,
per-unit outcomes, warnings, equipment classification, armor-core diagnostics,
and runtime identity audit. A null companion is different from an empty file.
`IGameResourceSource` supplies full unit payloads and archive resource tables;
`IUnitResourceSource` remains sufficient for ordinary version/layout updates.
`GameArchives.ReadResourcePayload` also supports non-unit resources.

`RepairPatch(ReadOnlyMemory<byte>)` is a strict main-buffer API. It cannot
validate companions, recover legacy layouts, classify equipment, or replace
culling. Requesting current-game culling through this API throws; use `RepairSet`.

## Policies

`RepairOptions` defaults to:

- `MissingUnits = Keep`: preserve unavailable units with a warning that their
  compatibility could not be verified. `Drop` and `Fail` are explicit options.
- `Lod = EquipmentSafe`: preserve custom LOD groups while applying supported
  version/layout upgrades. Canonical armor/helmet IDs and weighted resource/ref
  fingerprints provide equipment diagnostics. Unclassified mods also retain
  their LODs conservatively and report that decision. `Preserve` suppresses this
  classification warning; `CurrentGame` explicitly opts into vanilla LOD replacement
  on version changes. Already-current units are preserved byte for byte.
- `Culling = Patch`: preserve patch culling and companion payloads.
  `CurrentGameArms` imports current-game culling only for canonical slim/stocky
  left/right arms. It preserves visible meshes, merges required transforms and
  BoneInfo records, imports culling GPU data, and rebuilds matching TOC/companion
  offsets. Body, waist, legs, helmet, and custom units retain patch culling.
  If any eligible arm fails, the entire culling pass falls back to patch culling
  with a warning. Culling is disabled for recovered legacy patches.
- `RecoverLegacy = true`: recover an impossible TOC body offset only when exactly
  one complete unclaimed payload gap matches its declared size. Valid physical
  offsets are authoritative regardless of TOC row order. Recovered output is
  rebuilt by `(FileId, TypeId)` and every retained sidecar payload is audited.

Unknown enums, overlapping main bodies, duplicate resource keys, malformed ranges,
unsupported unit downgrades, and failed output audits are rejected. Cancellation
propagates. The engine never publishes partially repaired units after a format
error. Optional culling may repack buffers and retain unused source streams;
reapplying it can append data again.

## Staging and application integration

`RepairFileAsync(source, destination)` accepts a main patch, reads its companions,
validates the complete output, stages all files, and publishes the main file last.
Destinations must not exist. Folder repair discovers `<16 hex>.patch` and
`.patch_<decimal slot>`, preserves nested paths, and requires staging outside the
source tree. Companions are never standalone repair inputs. Failures are reported
per file; callers decide whether to deploy a partially successful batch.
Publication is not a crash-proof multi-file transaction; abandoned staging can be
discarded.

Quartermaster's adapter, deployment staging, repair preflight, and ZIP export use
the complete patch-set contract. Deployment hashes describe the repaired bytes;
source hashes continue to describe imported originals. Newly generated companions
are tracked explicitly against their source main patch. Callers retain warning
messages and pass consistent repair options through preflight, deployment, and
export.

## Tests and operational limits

Run `dotnet test Quartermaster.slnx`. Tests use committed JSON golden fixtures and
synthetic archives. They do not require an examples checkout, installed game,
Python, or Rust.

Fixture generators are development tools and require an explicit source checkout:

```sh
python3 tests/Quartermaster.Repatcher.Tests/generate_reference.py /path/to/hd2-repatcher
python3 tests/Quartermaster.Repatcher.Tests/generate_rust_reference.py /path/to/hd2-mod-crates
```

The Rust generator requires a development toolchain to regenerate its fixtures.
The application and C# tests consume committed fixture data directly.

Buffers are limited to .NET array sizes. Indexing and repair assume stable game
assets; reopen archive indexes after game updates. Sources used concurrently must
support concurrent reads. Structural and preservation checks do not establish
in-game compatibility for every mod.
