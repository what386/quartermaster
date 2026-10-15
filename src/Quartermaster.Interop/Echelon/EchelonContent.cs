using System.IO.Compression;
using System.Text.Json;
using Quartermaster.Library.Importing;
using Quartermaster.Library.Mods;

namespace Quartermaster.Interop.Echelon;

internal static class EchelonContent
{
    public static async Task<Mod> ImportAsync(IModContentStore contents, EchelonMod mod, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "quartermaster-echelon-" + Guid.NewGuid().ToString("N") + ".zip");
        var limits = new ImportLimits();
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                var images = new Dictionary<string, string>(StringComparer.Ordinal);
                string? Image(string? path)
                {
                    if (path is null) return null;
                    if (!images.TryGetValue(path, out var relative))
                        images.Add(path, relative = "images/" + images.Count + Path.GetExtension(path));
                    return relative;
                }
                var manifest = new
                {
                    Version = 1,
                    Guid = mod.ManifestId,
                    mod.Name,
                    mod.Description,
                    ModVersion = mod.Version,
                    IconPath = Image(mod.Image),
                    Options = mod.Options.Select(option => new
                    {
                        option.Name,
                        option.Description,
                        Image = Image(option.Image),
                        Include = option.Sets.Select(Folder).ToArray(),
                        SubOptions = option.Choices.Select(choice => new { choice.Name, choice.Description, Image = Image(choice.Image), Include = choice.Sets.Select(Folder).ToArray() }).ToArray()
                    }).ToArray()
                };
                await using (var stream = zip.CreateEntry("manifest.json").Open())
                    await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: ct).ConfigureAwait(false);
                long bytes = 0;
                var files = 1;
                for (var index = 0; index < mod.Sets.Count; index++)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var relative in mod.Sets[index])
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = EchelonReader.ResolveFile(mod.Directory, relative);
                        var name = Path.GetFileName(path);
                        if (!names.Add(name)) throw new InvalidDataException("Duplicate file in an Echelon patch set.");
                        await using var input = File.OpenRead(path);
                        if (++files > limits.MaxFiles || input.Length > limits.MaxBytes - bytes) throw new InvalidDataException("Echelon mod exceeds the import size or file limit.");
                        bytes += input.Length;
                        await using var destination = zip.CreateEntry(Folder(index) + "/" + name, CompressionLevel.NoCompression).Open();
                        await input.CopyToAsync(destination, ct).ConfigureAwait(false);
                    }
                }
                foreach (var (path, relative) in images)
                {
                    ct.ThrowIfCancellationRequested();
                    EchelonReader.ResolveFile(Path.GetDirectoryName(path)!, Path.GetFileName(path));
                    await using var input = File.OpenRead(path);
                    if (++files > limits.MaxFiles || input.Length > limits.MaxBytes - bytes) throw new InvalidDataException("Echelon artwork exceeds the import size or file limit.");
                    bytes += input.Length;
                    await using var destination = zip.CreateEntry(relative, CompressionLevel.NoCompression).Open();
                    await input.CopyToAsync(destination, ct).ConfigureAwait(false);
                }
            }
            return await contents.ImportAsync(temporary, mod.Name, ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Folder(int index) => "sets/" + index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);
}
