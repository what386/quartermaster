using System.Text;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Archives;

/// <summary>Supplies the current installed-game unit bytes. Implementations must allow concurrent reads.</summary>
public interface IUnitResourceSource
{
    bool ContainsUnit(ulong unitId);
    byte[] ReadUnit(ulong unitId, CancellationToken cancellationToken = default);
}

/// <summary>Read-only, instance-scoped access to legacy, DSAR and bundled game archives.</summary>
public sealed class GameArchives : IUnitResourceSource
{
    private sealed record BundleEntry(ulong ArchiveOffset, ulong BundleOffset, byte BundleIndex);
    private sealed record Package(ulong Size, BundleEntry[] Entries);
    private sealed record UnitLocation(string Package, ulong Offset, int Size);
    private readonly string directory;
    private readonly Dictionary<string, Package> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DsarReader> dsar = new(StringComparer.Ordinal);
    private readonly Dictionary<ulong, UnitLocation> units = new();
    public bool IsSlim { get; }
    public int UnitCount => units.Count;
    public IEnumerable<ulong> UnitIds => units.Keys;
    public string DataDirectory => directory;

    public static bool IsValidDataDirectory(string path) => Directory.Exists(path) &&
        (File.Exists(Path.Combine(path, "9ba626afa44a3aa3")) || File.Exists(Path.Combine(path, "bundles.nxa")));

    private GameArchives(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        if (!IsValidDataDirectory(this.directory)) throw new DirectoryNotFoundException("Not a Helldivers 2 data directory.");
        IsSlim = !File.Exists(Path.Combine(this.directory, "9ba626afa44a3aa3"));
    }

    /// <summary>Indexes unit locations without retaining open game files. Run off the UI thread.</summary>
    public static GameArchives Open(string directory, CancellationToken cancellationToken = default)
    {
        var archives = new GameArchives(directory);
        archives.Initialize(cancellationToken);
        return archives;
    }

    private void Initialize(CancellationToken cancellationToken)
    {
        IEnumerable<string> names;
        if (IsSlim)
        {
            LoadBundleIndex(cancellationToken);
            var database = File.ReadAllBytes(Path.Combine(directory, "bundle_database.data"));
            if (BinaryData.U32(database, 0) != 3)
                throw new InvalidDataException("Unsupported bundle database version.");
            var count = BinaryData.U32(database, 4);
            BinaryData.Slice(database, 8, 4L * count);
            var list = new List<string>();
            long cursor = 8;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileCount = BinaryData.U32(database, cursor);
                cursor += 4;
                BinaryData.Slice(database, cursor, 4L * fileCount);
                for (var j = 0; j < fileCount; j++)
                {
                    var length = BinaryData.U32(database, cursor);
                    cursor += 4;
                    var name = Encoding.UTF8.GetString(BinaryData.Slice(database, cursor, length));
                    cursor += length;
                    ValidatePackageName(name);
                    if (IsArchiveName(name)) list.Add(name);
                }
            }
            if (cursor != database.Length) throw new InvalidDataException("Unexpected trailing bundle database bytes.");
            names = list.Distinct(StringComparer.Ordinal);
        }
        else
        {
            names = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(p => IsArchiveName(Path.GetFileName(p)))
                .Select(p => Path.GetRelativePath(directory, p)).Order(StringComparer.Ordinal);
        }

        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                using var file = File.OpenRead(path);
                var magic = BinaryData.U32(BinaryData.ReadAt(file, 0, 4), 0);
                if (magic == DsarReader.Magic) dsar.Add(name, new DsarReader(path));
                else if (magic != PatchTable.Magic) continue;
            }
            else if (!packages.ContainsKey(name)) continue;
            var header = ReadPackage(name, 0, 72, cancellationToken);
            var length = BinaryData.Length(72UL + 32UL * BinaryData.U32(header, 4) + 80UL * BinaryData.U32(header, 8));
            var table = PatchTable.Read(ReadPackage(name, 0, length, cancellationToken));
            foreach (var record in table.Resources.Where(r => r.TypeId == PatchTable.UnitTypeId))
            {
                if (record.DataOffset < (ulong)table.DataStart || record.DataSize < 0x74)
                    throw new InvalidDataException("Invalid installed unit resource.");
                var location = new UnitLocation(name, record.DataOffset, BinaryData.Length(record.DataSize));
                if (units.TryGetValue(record.FileId, out var previous))
                {
                    // Duplicated resources must agree; never pick an arbitrary archive's version.
                    if (!ReadPackage(previous.Package, previous.Offset, previous.Size, cancellationToken)
                        .AsSpan().SequenceEqual(ReadPackage(name, location.Offset, location.Size, cancellationToken)))
                        throw new InvalidDataException($"Conflicting installed copies of unit {record.FileId:x16}.");
                }
                else units.Add(record.FileId, location);
            }
        }
        if (units.Count == 0) throw new InvalidDataException("No unit resources found in the game data directory.");
    }

    public bool ContainsUnit(ulong unitId) => units.ContainsKey(unitId);

    public byte[] ReadUnit(ulong unitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!units.TryGetValue(unitId, out var unit)) throw new KeyNotFoundException($"Unit {unitId:x16} not found.");
        return ReadPackage(unit.Package, unit.Offset, unit.Size, cancellationToken);
    }

    private byte[] ReadPackage(string name, ulong offset, int size, CancellationToken cancellationToken)
    {
        if (dsar.TryGetValue(name, out var compressed)) return compressed.Read(offset, size, cancellationToken);
        var path = Path.Combine(directory, name);
        if (File.Exists(path))
        {
            using var file = File.OpenRead(path);
            return BinaryData.ReadAt(file, offset, size);
        }
        if (!packages.TryGetValue(name, out var package)) throw new InvalidDataException($"Missing package: {name}");
        if (offset > package.Size || size < 0 || (ulong)size > package.Size - offset)
            throw new InvalidDataException("Bundled resource exceeds package size.");
        var result = new byte[size];
        var written = 0;
        for (var i = 0; i < package.Entries.Length && written < size; i++)
        {
            var entry = package.Entries[i];
            var end = i + 1 < package.Entries.Length ? package.Entries[i + 1].ArchiveOffset : package.Size;
            var position = offset + (ulong)written;
            if (position < entry.ArchiveOffset || position >= end) continue;
            var length = (int)Math.Min((ulong)(size - written), end - position);
            var bundle = dsar[BundleName(entry.BundleIndex)];
            bundle.Read(checked(entry.BundleOffset + position - entry.ArchiveOffset), length, cancellationToken)
                .CopyTo(result, written);
            written += length;
        }
        if (written != size) throw new InvalidDataException("Incomplete bundled package.");
        return result;
    }

    private void LoadBundleIndex(CancellationToken cancellationToken)
    {
        var index = new DsarReader(Path.Combine(directory, "bundles.nxa"));
        var data = index.Read(0, BinaryData.Length(index.Length), cancellationToken);
        var bundleCount = BinaryData.U32(data, 12);
        var packageCount = BinaryData.U32(data, 16);
        BinaryData.Slice(data, 24, 24L * packageCount);
        for (var i = 0; i < packageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = BinaryData.Slice(data, 24L + i * 24L, 24);
            var size = BinaryData.U64(row, 0);
            var nameOffset = BinaryData.U32(row, 8);
            var nameBytes = BinaryData.Slice(data, nameOffset, data.Length - (long)nameOffset);
            var terminator = nameBytes.IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("Unterminated bundle package name.");
            var name = Encoding.UTF8.GetString(nameBytes[..terminator]);
            ValidatePackageName(name);
            var count = BinaryData.U32(row, 12);
            var entriesData = BinaryData.Slice(data, BinaryData.U32(row, 16), count * 16L);
            var entries = new BundleEntry[(int)count];
            for (var j = 0; j < entries.Length; j++)
            {
                var item = entriesData.Slice(j * 16, 16);
                var entry = new BundleEntry(BinaryData.U64(item, 0), BinaryData.U32(item, 8), item[15]);
                if (entry.BundleIndex >= bundleCount || entry.ArchiveOffset >= size ||
                    (j == 0 ? entry.ArchiveOffset != 0 : entry.ArchiveOffset <= entries[j - 1].ArchiveOffset))
                    throw new InvalidDataException("Invalid bundled package entry.");
                entries[j] = entry;
                var bundleName = BundleName(entry.BundleIndex);
                if (!dsar.ContainsKey(bundleName)) dsar.Add(bundleName, new DsarReader(Path.Combine(directory, bundleName)));
            }
            if (size > 0 && entries.Length == 0) throw new InvalidDataException("Package has no bundle entries.");
            if (!packages.TryAdd(name, new(size, entries))) throw new InvalidDataException("Duplicate package name.");
        }
    }

    private static string BundleName(byte index) => $"bundles.{index:00}.nxa";
    private static bool IsArchiveName(string name) => name.Length == 16 && name.All(char.IsAsciiHexDigit);
    private static void ValidatePackageName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
            throw new InvalidDataException("Unsafe bundle package name.");
    }
}
