using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.FileMerger;

public sealed record DetectionLimits(int MaxFiles = 10000, int MaxFileBytes = 256 * 1024 * 1024, long MaxTotalBytes = 2L * 1024 * 1024 * 1024);

/// <summary>Reads supported payloads from ZIPs, directories, patch resources, streams and generated_*.dl_bin files.
/// Never loads or executes a mod's installer or Lua code. Each result retains its source path for variant selection.</summary>
public sealed class FileModDetector
{
    private readonly DetectionLimits limits;
    private const ulong LuaType = 0xa14e8dfa2cd117e2;
    public FileModDetector(DetectionLimits? limits = null)
    {
        this.limits = limits ?? new();
        if (this.limits.MaxFiles <= 0 || this.limits.MaxFileBytes <= 0 || this.limits.MaxTotalBytes <= 0)
            throw new ArgumentException("Detection limits must be positive.");
    }

    public async Task<FileModDetection> DetectAsync(string source, CancellationToken ct = default)
    {
        source = Path.GetFullPath(source);
        var mods = new List<FileMod>(); var installers = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0; long total = 0;
        async Task Read(string name, long size, Func<Stream> open)
        {
            ct.ThrowIfCancellationRequested();
            var path = name.Replace('\\', '/');
            if (path.StartsWith('/') || path.Split('/').Any(part => part is ".." or "." || part.Contains(':')))
                throw new InvalidDataException($"Unsafe mod archive path: {name}");
            if (++count > limits.MaxFiles || size < 0 || size > limits.MaxFileBytes || size > limits.MaxTotalBytes - total)
                throw new InvalidDataException("File mod exceeds detection size/file limits.");
            total += size;
            if (!names.Add(path)) throw new InvalidDataException($"Duplicate mod archive path: {path}");
            if (!IsConfig(path) && !IsPatch(path) && !path.EndsWith(".stream", StringComparison.OrdinalIgnoreCase)) return;
            await using var stream = open();
            var bytes = new byte[checked((int)size)];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (stream.ReadByte() != -1) throw new InvalidDataException($"Mod file exceeds its declared size: {path}");
            var identity = source + "!" + path;
            try { Inspect(bytes, path, identity, mods, installers, ct); }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
            { throw new InvalidDataException($"Could not detect file mod '{identity}': {ex.Message}", ex); }
        }
        if (Directory.Exists(source))
        {
            foreach (var file in Enumerate(source))
                await Read(Path.GetRelativePath(source, file), new FileInfo(file).Length, () => File.OpenRead(file)).ConfigureAwait(false);
        }
        else if (Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var entry in zip.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                if (((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InvalidDataException("File-mod ZIP contains a symbolic link.");
                if (entry.FullName.EndsWith('/')) continue;
                await Read(entry.FullName, entry.Length, entry.Open).ConfigureAwait(false);
            }
        }
        else
        {
            RejectLink(source);
            await Read(Path.GetFileName(source), new FileInfo(source).Length, () => File.OpenRead(source)).ConfigureAwait(false);
        }
        return new(mods.ToArray(), installers.Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Detects an already verified stored file without reopening it from disk.</summary>
    public FileModDetection DetectFile(string filename, ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (content.Length > limits.MaxFileBytes) throw new InvalidDataException("File mod exceeds detection size limits.");
        var mods = new List<FileMod>(); var installers = new List<string>();
        Inspect(content.ToArray(), filename, filename, mods, installers, ct);
        return new(mods.ToArray(), installers.ToArray());
    }

    private static bool IsConfig(string path) => Path.GetFileName(path).StartsWith("generated_", StringComparison.Ordinal) && path.EndsWith(".dl_bin", StringComparison.Ordinal);
    private static bool IsPatch(string path)
    {
        var name = Path.GetFileName(path);
        var at = name.IndexOf(".patch", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;
        var suffix = name[(at + 6)..];
        return suffix == "" || suffix.StartsWith('_') && suffix.Length > 1 && suffix.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private static void Inspect(byte[] bytes, string path, string source, List<FileMod> mods, List<string> installers, CancellationToken ct)
    {
        if (IsConfig(path))
        {
            ConfigDocument.Parse(GameConfigCodec.Decrypt(bytes));
            mods.Add(new(Path.GetFileNameWithoutExtension(path), FileModPaths.ValidateTarget("data/game/" + Path.GetFileName(path)), bytes, source, []));
        }
        else if (IsPatch(path))
        {
            var table = PatchTable.Read(bytes);
            foreach (var resource in table.Resources.Where(resource => resource.TypeId == LuaType))
            {
                ct.ThrowIfCancellationRequested();
                if (resource.DataOffset > (ulong)bytes.Length || resource.DataSize > (ulong)bytes.Length - resource.DataOffset)
                    throw new InvalidDataException("Patch resource is outside its file.");
                var body = bytes.AsSpan((int)resource.DataOffset, (int)resource.DataSize);
                if (body.Length < 8) continue;
                // A Lua resource has a u32 content length and u32 kind before its contents.
                if (body[8..].StartsWith("-- HD2 file-mod installer 1 "u8)) installers.Add(source);
                if (body[8..].StartsWith("HD2FM1"u8))
                {
                    if (BinaryPrimitives.ReadUInt32LittleEndian(body) != body.Length - 8 || BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) != 2)
                        throw new InvalidDataException("Invalid HD2FM1 Lua-resource framing.");
                    mods.Add(ParsePayload(body[8..], source + "#" + resource.FileId.ToString("x16", CultureInfo.InvariantCulture)).Mod);
                }
            }
        }
        else
        {
            // Stream bundles can contain several independently framed HD2FM1 payloads.
            var offset = 0;
            while (offset < bytes.Length)
            {
                ct.ThrowIfCancellationRequested();
                var relative = bytes.AsSpan(offset).IndexOf("HD2FM1"u8);
                if (relative < 0) break;
                offset += relative;
                var parsed = ParsePayload(bytes.AsSpan(offset), source + "@" + offset);
                mods.Add(parsed.Mod); offset += parsed.Consumed;
            }
        }
    }

    internal static (FileMod Mod, int Consumed) ParsePayload(ReadOnlySpan<byte> data, string source)
    {
        if (data.Length < 10 || !data.StartsWith("HD2FM1"u8)) throw new InvalidDataException("Invalid HD2FM1 payload.");
        var length = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);
        if (length > 1024 * 1024 || length > data.Length - 10) throw new InvalidDataException("Invalid HD2FM1 metadata length.");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var stock = new List<string>(); var modified = new List<string>();
        foreach (var raw in new UTF8Encoding(false, true).GetString(data.Slice(10, (int)length)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r'); var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Malformed HD2FM1 metadata line.");
            var key = line[..equals]; var value = line[(equals + 1)..];
            if (key == "stock") stock.Add(value);
            else if (key == "mod") modified.Add(value);
            else if (!fields.TryAdd(key, value)) throw new InvalidDataException($"Duplicate HD2FM1 field: {key}");
        }
        string Required(string key) => fields.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"HD2FM1 is missing {key}.");
        if (!int.TryParse(Required("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size < 64 || size > data.Length - 10 - (int)length)
            throw new InvalidDataException("HD2FM1 config length is invalid or truncated.");
        var content = data.Slice(10 + (int)length, size).ToArray();
        if (stock.Count == 0 || !modified.Contains(FileModPaths.Fingerprint(content), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("HD2FM1 config fingerprint is missing or does not match its payload.");
        var target = FileModPaths.ValidateTarget(Required("target"));
        ConfigDocument.Parse(GameConfigCodec.Decrypt(content));
        return (new(Required("name"), target, content, source, stock.ToArray(), fields.GetValueOrDefault("version")), checked(10 + (int)length + size));
    }

    internal static void RejectLink(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Symbolic links are not supported for file-mod paths: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static IEnumerable<string> Enumerate(string root)
    {
        RejectLink(root);
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            RejectLink(path);
            if (Directory.Exists(path)) foreach (var file in Enumerate(path)) yield return file;
            else yield return path;
        }
    }
}
