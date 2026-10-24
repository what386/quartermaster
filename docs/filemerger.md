# File merger implementation

`Quartermaster.FileMerger` is the .NET engine for detecting HELLDIVERS 2
settings-file payloads, combining their config changes, and applying or restoring
the resulting game files. It owns binary compatibility checks, conflict reporting,
hash-preserving output, backups, and config journals. Library identity, profile
selection, archive deployment, and user interaction are owned by callers.

## Entry points and contracts

| API | Contract |
| --- | --- |
| `FileModDetector.DetectAsync(source, CancellationToken)` | Reads a ZIP, directory, patch, stream bundle, or generated config and returns supported payload candidates and recognized installer paths. |
| `FileModDetector.DetectFile(filename, content, CancellationToken)` | Inspects an already verified buffer without reopening its source file. The filename determines the container format. |
| `ConfigMerger.Merge(target, stock, orderedMods, CancellationToken)` | Combines one target's changes against stock, returning sealed output, value conflicts, and skipped replacements. |
| `GameConfigCodec.Decrypt` / `Encrypt` | Authenticate and decode, or seal, a game config. Encryption alone does not preserve the expected game hash. |
| `GameConfigCodec.EncryptPreservingHash(plain, gameHash)` | Validates LDLD framing and adds a correction trailer when needed to retain the supplied hash. |
| `FileModInstaller.PrepareAsync(gameDirectory, orderedMods, CancellationToken)` | Resolves verified originals and prepares all config writes, including restoration of deselected targets. Does not write game files. |
| `FileModInstaller.ApplyAsync(plan, CancellationToken)` | Revalidates a prepared snapshot, saves originals, journals, publishes, and verifies the config files. |
| `FileModInstaller.RestoreAsync(gameDirectory, CancellationToken)` | Restores verified originals, including recovery from an interrupted config operation. |
| `FileModInstaller.InspectAsync(gameDirectory, CancellationToken)` | Reports journal completion and whether each managed target matches its applied hash. |

`FileMod.Content` is the sealed config, not its patch container or installer.
`Source` identifies the candidate's file, resource, or stream frame and must be
retained for diagnostics and skipped-input attribution. Detection returns every
supported candidate, including mutually exclusive variants; it does not choose a
loadout. Inputs are ordered from lowest to highest priority: the last mod wins.

Keep supplied buffers stable while processing. Merge does not mutate stock or
candidate buffers. Apply only unmodified plans returned by the same installer's
prepare method. `gameDirectory` is the installation root containing `data/game`,
not the game's `data` directory. The caller must keep the game and other config
editors stopped during publication.

```csharp
var detection = await new FileModDetector().DetectAsync(source, ct);
// Resolve enabled variants and arrange their priority before preparing.
IReadOnlyList<FileMod> selected = SelectPayloads(detection.Mods);

var installer = new FileModInstaller(applicationStateDirectory);
var plan = await installer.PrepareAsync(gameDirectory, selected, ct);

// Retain plan.Conflicts and plan.Skipped for review before committing.
await installer.ApplyAsync(plan, ct);
// Later, prepare a changed selection or restore all managed configs.
await installer.RestoreAsync(gameDirectory, ct);
```

## Processing flow

[`ConfigMerger`](../src/Quartermaster.FileMerger/Merging/ConfigMerger.cs) processes
one target at a time:

1. Validate the target, authenticate stock, parse its LDLD blocks, and compute its
   seeded game hash and package fingerprint.
2. Validate each candidate's target and stock-fingerprint list, authenticate its
   config, and require its game hash to match stock.
3. Compare block layout and framing with stock, accounting for the supported
   final hash-correction trailer.
4. If any candidate changes the layout, return only the highest-priority
   candidate's sealed bytes and report every other candidate as skipped.
5. Otherwise compare payloads with stock in 32-bit cells, apply changed cells in
   priority order, and record conflicting writes.
6. If gameplay bytes changed, generate a hash-preserving trailer, seal the
   result, and decrypt it again to verify encryption output.

`FileModInstaller.PrepareAsync` groups selected candidates by normalized target
and invokes this process independently for each file. It also prepares original
bytes for managed targets that no longer have active payloads. Repeated deployment
uses verified backups as the baseline rather than accumulating edits on the last
merged output.

## Detection, framing, and input limits

[`FileModDetector`](../src/Quartermaster.FileMerger/Detection/FileModDetector.cs)
reads patch resource tables through `PatchTable`. A supported Lua resource has
an eight-byte wrapper containing a little-endian content length and kind `2`,
followed by an `HD2FM1` payload. Resource ranges and recognized payload wrappers
are checked before parsing their contents. The shared installer is identified by
its `-- HD2 file-mod installer 1 ` prefix and reported separately; it is never
executed.

An HD2FM1 frame contains:

1. The six-byte `HD2FM1` marker.
2. A little-endian `u32` metadata byte length, limited to 1 MiB.
3. UTF-8 `key=value` lines. `name`, `target`, and `size` are required; `version`
   is optional. `stock` and `mod` may repeat; duplicate scalar keys are rejected.
4. Exactly `size` bytes of sealed config content for that frame.

At least one stock fingerprint is required, and the sealed payload must match a
listed `mod` fingerprint. A fingerprint consists of the ciphertext length and
32-byte samples from its start, midpoint, and end. It identifies packaged file
versions; it is not a cryptographic hash. Streams can contain several framed
payloads; scanning resumes after each complete frame.

Plain `generated_*.dl_bin` candidates are authenticated and parsed directly. Their
target is derived from the filename, and they have no package stock-fingerprint
list. For these candidates, the caller must establish trusted stock beforehand:
another editor can preserve the same game hash, so that hash cannot prove that a
file is pristine.

| `DetectionLimits` field | Default |
| --- | --- |
| `MaxFiles` | 10,000 |
| `MaxFileBytes` | 256 MiB |
| `MaxTotalBytes` | 2 GiB |

ZIP and directory inspection enforce these limits and reject symbolic links,
unsafe relative paths, duplicate file paths, and truncated or oversized streams.
Directory traversal and ZIP entries are processed deterministically. Ordinary
Lua/model/audio resources produce no settings candidates. Arbitrary installer
programs, executable toggles, and legacy `SBSP1` payloads are not interpreted.

## Config parsing and hash preservation

[`GameConfigCodec`](../src/Quartermaster.FileMerger/Formats/GameConfigCodec.cs)
uses NaCl sealed boxes with the game's format keys. Sealed-box overhead is 48
bytes. SpaceWizards.Sodium supplies the managed binding and bundled libsodium
native assets; no mod code or external installer is invoked.

[`ConfigDocument`](../src/Quartermaster.FileMerger/Formats/ConfigDocument.cs)
parses a sequence of LDLD blocks. Each header contains:

| Offset | Field |
| --- | --- |
| `0x00` | `LDLD` magic and little-endian version `1` |
| `0x08` | `u32` block type |
| `0x0c` | `u32` payload size |
| `0x10` | Flag byte, required to be `1` |
| `0x11..0x17` | Reserved/correction bytes |
| `0x18` | Payload start |

The parser searches up to 56 bytes from the current cursor for the next header,
checks payload bounds, and advances to the payload's end. Block types may repeat;
block index remains part of conflict identity. This parser establishes framing,
not the semantic meaning of tables, records, or stat fields.

[`ConfigHash`](../src/Quartermaster.FileMerger/Formats/ConfigHash.cs) implements
MurmurHash64A with seed `16045690983977655786`, multiplier
`0xc6a4a7935bd1e995`, and little-endian words. The config hash is computed over the
plaintext; SHA-256 fingerprints used for storage and deployment track sealed file
bytes separately.

Correction extends the final LDLD payload with zero padding and one aligned
64-bit word, updates that block's size, and solves the word using the inverse
hash operations. The resulting plaintext must hash to the stock value. Requested
gameplay values are neither approximated nor used as correction storage. The
corrected framing is reparsed before encryption. Encryption is randomized, so
ciphertext hashes are unsuitable as stable profile-selection signatures.

## Merge policies and preservation invariants

| Condition | Behavior |
| --- | --- |
| Distinct cells differ from stock | Combine their changes. |
| Candidates write identical bytes to one cell | Preserve the value without a conflict. |
| Candidates write different bytes to one cell | Last candidate wins; report both mod names and the cell location. |
| Any candidate changes unsupported layout details | Install only the highest-priority candidate for that target, unchanged; report all others as skipped. |
| Package does not recognize stock, or plaintext game hashes differ | Fail preparation with the candidate's name, source, and target. |

Cells are four bytes relative to each block's payload, with a final short cell
when necessary. Comparing complete cells prevents partial byte edits from being
combined into an integer or float that no candidate supplied. This is a binary
cell merger, not a schema-aware field merger.

Compatible candidates retain block count, order, types, header positions, and
inter-block framing bytes. Payload sizes must match except for a final
8–15-byte correction trailer: any bytes preceding its last eight bytes must be
zero. That trailer is excluded from gameplay comparisons and regenerated for the
combined result. Reserved header correction bytes are not merged as payload data.

A genuine insertion, deletion, reordered block, or changed payload layout takes
the exclusive-replacement path. Matching values by record or field identity and
rebuilding their internal offsets are outside this implementation. Different file
sizes are therefore supported only for the recognized correction convention or
as an exclusive replacement. Other target files still merge independently.

## Results and error handling

`FileModDetection.Mods` contains candidates; `InstallerFiles` contains recognized
helper paths. Neither collection is a deployment request.

`MergeResult.Content` contains complete sealed output for one target.
`MergeConflict` identifies target, block type/index, payload-relative cell offset,
and losing/winning mod names. `SkippedFileMod` records target, name, reason, and
source attribution. Retain these reports even when the resulting output is valid.
An empty input selection returns stock bytes; inputs that introduce no gameplay
changes also retain the original ciphertext.

Malformed configs, failed authentication, incompatible versions, and invalid
payload framing prevent output through exceptions. Filesystem entry points also
propagate I/O/access failures. Cancellation propagates rather than selecting a
fallback or returning a partially prepared plan. Detection and preparation do not
collect per-file failures and continue with a reduced selection.

## Staging, journals, and restoration

[`FileModInstaller`](../src/Quartermaster.FileMerger/Installation/FileModInstaller.cs)
maintains a per-installation state directory and exclusive operation lock.
Original ciphertext backups are addressed by SHA-256 and retained after restore.
Plans contain original and output bytes, expected current-file hashes, and the
revision of the state document used during preparation.

Apply checks the state revision and all target snapshots, authenticates and parses
original/output configs, and requires matching game hashes. It verifies or saves
backups, stages every output beside its target, and verifies staged bytes before
writing a pending journal. Targets are then rechecked, atomically replaced one at
a time, and verified. Only after publication is the journal marked complete;
restored targets are removed from the active managed-file list.

Ordinary failures after journaling attempt to restore published files and the
previous journal. Rollback does not overwrite a file that changed again after
publication. Failed recovery raises an aggregate error and leaves recovery state
available. `RestoreAsync` accepts journaled original/applied bytes and, for pending
operations, the previous applied bytes. Backups and all current targets are checked
before any restoration writes. Interrupted journals require restore before a new
plan is prepared.

Targets are restricted to existing `data/game/generated_<name>.dl_bin` files and
normalized to lowercase. Caller-selected game and state roots are resolved to
physical directories, allowing installation aliases and linked app-data parents.
Installation identities use the resolved game root; aliases share journals and
backups. Links within managed config, journal, and backup paths remain rejected.
External changes, stale plans, and corrupt backups prevent overwrite.
After a game update, new stock may replace the old baseline only when every
incoming package explicitly recognizes its fingerprint. Raw candidates cannot
authorize that adoption. Restore never puts old stock over an updated game file.

Publication is recoverable across files, not a filesystem-wide transaction or a
guarantee against power-loss damage. Callers must retain the state directory and
backups, keep buffers/plans stable, and coordinate other game-file writers.

## Integration boundaries

[`FileModMetadata`](../src/Quartermaster.Library/Importing/FileModMetadata.cs)
classifies stored sets through `PatchSet.ContentKind`. It checks resource prefixes
before loading complete recognized payloads, so ordinary Lua inside a large asset
patch does not inherit the config detector's payload-size limit. Unsupported
resources sharing a recognized payload patch fail explicitly. Separate ordinary
asset sets remain on the archive deployment path.

`FileDetectionVersion` permits classification of existing imports without changing
patch/option IDs or saved selections. Cached classification is tied to set
metadata and source hashes. `Mod.IsFileBased` drives the file badge; recognized
payloads and helpers do not contribute to `Mod.UsesLua`. Hybrid packages with
ordinary Lua retain both badges.

Loose configs and streams use stored sets with synthetic archive identities and
payload indexes. These identities never reach archive deployment. Manifest
options remain the primary selection mechanism. Inferred folder choices group
alternatives affecting overlapping targets; independent target folders remain
selected together. Same-target frames in one stream get indexed choices.
Installer-only options are hidden because restoration replaces their function.
Portable profiles retain all original variants and selections.

[`ProfileFileMods`](../src/Quartermaster.Library/Profiles/ProfileFileMods.cs)
resolves enabled sets through `PatchSelection` and orders them through
`ProfileEditor.InDeploymentOrder`. It re-verifies selected source bytes and
re-detects payloads before planning. Selection signatures include source hashes,
set IDs, payload indexes, options, and effective order. Receipts and installed-file
health drive deployment outlines, launch warnings, and storage protection.

`LibrarySession` prepares the config merge before repatching or publication and
presents a structured review grouped by target file. Each group separates the
final winning mod from overridden or skipped mods. Conflict attribution must use
the final writer for each cell, including when three or more mods change it;
earlier edits equal to the final value do not conflict. Layout replacements
identify both the applied mod and every skipped source.

`LibrarySession` also queues merge previews when enabled mods, saved options,
priority, or loadout order change, and when the game/library is reloaded. Checks
run on workers after a 200 ms debounce. Superseded checks are cancelled, drained,
and guarded by a generation before publishing results on the UI thread. The active
profile is checked first; diagnostics are scoped to each profile's game path and
selection signature. `ModDiagnosticsChanged` updates row diagnostics without
rebuilding option editors or discarding unsaved choices. Background failures
populate error tooltips without raising an operation error toast or prompting for
merge approval.

Previews do not write configs, backups, or deployment receipts. They use the
planner's state lock and may create its lock directory/file. Inspection, Apply,
Purge, and shutdown cancel and drain previews before accessing that state. Apply
always prepares a fresh plan; a successful preview cannot authorize a stale write.
Decoded payload detections are cached by source path and hash, with at most eight
entries of 4 MiB each. Every plan still verifies source bytes and rechecks stock
compatibility. Unchanged diagnostic selections are reused between UI refreshes.

Receipts persist diagnostics by library mod ID. Loadout and library rows expose
confirmed merge conflicts and skipped file mods through the red error icon and
wrapped tooltip, falling back
to matching deployment receipts while a preview is pending. Selection signatures
prevent showing stale winners after options or order change. Repatch diagnostics use the
same warning system. Failed deployments expose a separate red error icon and
tooltip, attributed by structured mod ID or file-mod source when available.
Installation-wide failures apply to all enabled entries. Errors are scoped to the
attempted game, loadout, options, and source hashes, and clear on a successful
retry. Confirmed overlaps are errors even when priority chooses an output: edits
from overridden mods will be lost. Missing dependencies and repatch preservation
diagnostics remain warnings. Conflict messages name the config file and list the
applied, overridden, or skipped mods on separate lines, with blank lines between
sections. Messages describe loss of overlapping edits or whole-file replacement;
no binary-cell counts are presented as named settings. The merger does not yet
map those cells to setting names. Apply still allows an explicitly reviewed merge.

It writes a pending
receipt, deploys ordinary archive sets, applies configs, and completes the receipt.
`ProfilePatches` excludes recognized payloads and helpers from the archive request.
Purge restores verified configs before removing archive patches.

The archive ledger and config journal are separate recoverable operations. A
failed composite deployment leaves an unhealthy ledger or pending receipt rather
than a clean deployment status. App config state lives under `filemods` and profile
receipts under `filemod-receipts`, keyed per installation. These directories are
not staging-cleanup candidates.

## Maintenance and tests

Run `dotnet test tests/Quartermaster.FileMerger.Tests` for engine changes. Changes
to detection, selection, publication, or metadata also require library and GUI
integration checks; `dotnet test Quartermaster.slnx` covers all layers. Fixtures
are synthetic and do not require an installed game or ignored `examples/` files.

Add coverage for malformed frames and bounds, unchanged-byte preservation,
conflict precedence, genuine layout changes versus correction trailers, stale
versions, external edits, backup corruption, and interrupted publication when
extending formats. Update detection-version handling when stored classification
contracts change. Keep native runtime assets available for every shipped target.
See the [project README](../src/Quartermaster.FileMerger/README.md) and
[third-party notices](../src/Quartermaster.FileMerger/THIRD_PARTY_NOTICES.txt) for
API usage, format research, and dependencies. Structural and hash checks do not
establish in-game correctness of arbitrary config edits.
