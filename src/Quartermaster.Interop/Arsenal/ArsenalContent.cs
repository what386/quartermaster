using System.IO.Compression;
using System.Text.Json.Nodes;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;

namespace Quartermaster.Interop.Arsenal;

internal static class ArsenalContent
{
    public static async Task<Mod> ImportAsync(IModContentStore contents, ArsenalMod mod, CancellationToken ct)
    {
        var files = Enumerate(mod.Directory).ToArray();
        var manifests = files.Where(file => Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) return await contents.ImportAsync(mod.Directory, mod.Name, ct).ConfigureAwait(false);
        var manifestPath = manifests[0];
        var document = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false)) as JsonObject;
        if (document is null) return await contents.ImportAsync(mod.Directory, mod.Name, ct).ConfigureAwait(false);
        var changed = false;
        var version = document.FirstOrDefault(pair => pair.Key.Equals("Version", StringComparison.OrdinalIgnoreCase));
        // Some Arsenal manifests use Version for the mod release instead of the schema.
        if (version.Value is JsonValue release && release.TryGetValue<string>(out var releaseName))
        {
            if (!document.Any(pair => pair.Key.Equals("ModVersion", StringComparison.OrdinalIgnoreCase)))
                document["ModVersion"] = releaseName;
            document[version.Key] = 1;
            changed = true;
        }
        var property = document?.FirstOrDefault(pair => pair.Key.Equals("Options", StringComparison.OrdinalIgnoreCase));
        if (property?.Value is JsonArray options && options.Any(option => option is JsonValue value && value.TryGetValue<string>(out _)))
        {
            var converted = new JsonArray();
            foreach (var option in options)
                converted.Add(option is JsonValue value && value.TryGetValue<string>(out var name)
                    ? new JsonObject { ["Name"] = name, ["Include"] = new JsonArray(JsonValue.Create(name)) }
                    : option?.DeepClone());
            document![property!.Value.Key] = converted;
            changed = true;
        }
        if (!changed) return await contents.ImportAsync(mod.Directory, mod.Name, ct).ConfigureAwait(false);
        var limits = new ImportLimits();
        if (files.Length > limits.MaxFiles) throw new InvalidDataException("Arsenal mod exceeds the import file limit.");
        var temporary = Path.Combine(Path.GetTempPath(), "quartermaster-arsenal-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            long bytes = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var length = new FileInfo(file).Length;
                    if (length > limits.MaxBytes - bytes) throw new InvalidDataException("Arsenal mod exceeds the import size limit.");
                    bytes += length;
                    var entry = zip.CreateEntry(Path.GetRelativePath(mod.Directory, file).Replace('\\', '/'), CompressionLevel.NoCompression);
                    await using var destination = entry.Open();
                    if (file == manifestPath)
                    {
                        var data = System.Text.Encoding.UTF8.GetBytes(document!.ToJsonString());
                        await destination.WriteAsync(data, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await using var input = File.OpenRead(file);
                        await input.CopyToAsync(destination, ct).ConfigureAwait(false);
                    }
                }
            }
            return await contents.ImportAsync(temporary, mod.Name, ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static IEnumerable<string> Enumerate(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Arsenal mod contains a symbolic link.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Arsenal mod contains a symbolic link.");
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (var file in Enumerate(entry)) yield return file;
            else yield return entry;
        }
    }
}
