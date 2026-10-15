using System.Text.Json;
using Quartermaster.Repatcher.Archives;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Repair;

internal sealed class EquipmentSafety
{
    private sealed record Equipment(string Category, string Hash, string Name, Dictionary<string, ulong> Parts);
    private static readonly Lazy<Equipment[]> catalog = new(Load);
    private static readonly Lazy<HashSet<ulong>> arms = new(() => catalog.Value.Where(e => e.Category == "Armor")
        .SelectMany(e => e.Parts).Where(p => p.Key is "slim left arm" or "slim right arm" or "stocky left arm" or "stocky right arm")
        .Select(p => p.Value).ToHashSet());
    public static bool IsCanonicalArm(ulong id) => arms.Value.Contains(id);

    public IReadOnlyList<string> DetectedNames { get; }
    private readonly Equipment[] detectedEquipment;

    public EquipmentSafety(PatchDocument document, IUnitResourceSource source, CancellationToken ct)
    {
        var ids = document.Resources.Where(r => r.Record.TypeId == PatchTable.UnitTypeId).Select(r => r.Record.FileId).ToHashSet();
        if (ids.Count == 0) { DetectedNames = []; detectedEquipment = []; return; }
        var detected = catalog.Value.Where(e => e.Parts.Values.Any(ids.Contains)).ToList();
        if (source is IGameResourceSource game)
        {
            var fingerprint = Fingerprint(document);
            var archives = new Dictionary<string, HashSet<ulong>>();
            HashSet<ulong> ArchiveIds(string hash)
            {
                if (archives.TryGetValue(hash, out var cached)) return cached;
                ct.ThrowIfCancellationRequested();
                try { cached = game.ReadArchiveResources(hash, ct).Select(r => r.FileId).Where(id => id != 0).ToHashSet(); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or KeyNotFoundException) { cached = []; }
                archives[hash] = cached;
                return cached;
            }
            foreach (var category in new[] { "Armor", "Helmet" })
            {
                if (detected.Any(e => e.Category == category)) continue;
                var evidence = fingerprint.ToHashSet();
                foreach (var other in detected.Where(e => e.Category != category)) evidence.ExceptWith(ArchiveIds(other.Hash));
                var candidates = catalog.Value.Where(e => e.Category == category)
                    .Select(e => (Equipment: e, Hits: ArchiveIds(e.Hash).Intersect(evidence).ToArray())).Where(e => e.Hits.Length > 0).ToArray();
                var frequencies = candidates.SelectMany(e => e.Hits).GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
                var scores = candidates.Select(c => new
                {
                    c.Equipment, Count = c.Hits.Length,
                    Unique = c.Hits.Count(id => frequencies[id] == 1), Rare = c.Hits.Count(id => frequencies[id] <= 4),
                    Score = c.Hits.Sum(id => frequencies[id] switch { 1 => 100, 2 => 60, <= 4 => 30, <= 8 => 15, <= 16 => 5, _ => 1 })
                }).OrderByDescending(c => c.Score).ThenByDescending(c => c.Unique).ThenByDescending(c => c.Rare)
                    .ThenByDescending(c => c.Count).ThenBy(c => c.Equipment.Name, StringComparer.Ordinal).ThenBy(c => c.Equipment.Hash, StringComparer.Ordinal).ToArray();
                if (scores.Length == 0) continue;
                var best = scores[0];
                var second = scores.Length > 1 ? scores[1].Score : 0;
                var strong = category == "Armor"
                    ? best.Count >= 2 && (best.Unique >= 2 || best.Unique >= 1 && best.Score >= 130 && best.Score >= second + 40 ||
                        best.Rare >= 4 && best.Score >= 120 && best.Score * 100 >= second * 125)
                    : best.Count >= 2 && (best.Unique >= 1 || best.Rare >= 2 || best.Score >= 80 && best.Score >= second + 30) ||
                        best.Unique >= 1 && best.Score >= 100 && best.Score >= second + 50;
                if (strong) detected.Add(best.Equipment);
            }
        }
        DetectedNames = detected.Select(e => $"{e.Category}: {e.Name}").Distinct().Order(StringComparer.Ordinal).ToArray();
        detectedEquipment = detected.ToArray();
    }

    public IReadOnlyList<ArmorCoreAudit> AuditArmor(PatchDocument document)
    {
        var ids = document.Resources.Where(r => r.Record.TypeId == PatchTable.UnitTypeId).Select(r => r.Record.FileId).ToHashSet();
        return detectedEquipment.Where(e => e.Category == "Armor").Select(e =>
        {
            var present = e.Parts.Where(p => ids.Contains(p.Value)).ToArray();
            var visual = present.Count(p => document.Resources.Any(r => r.Record.TypeId == PatchTable.UnitTypeId &&
                r.Record.FileId == p.Value && (r.Record.GpuSize > 0 || HasDrawIndices(r.Data.Span))));
            return new ArmorCoreAudit(e.Name, present.Length, visual, e.Parts.Where(p => !ids.Contains(p.Value)).Select(p => p.Key).ToArray());
        }).ToArray();
    }

    private static bool HasDrawIndices(ReadOnlySpan<byte> data)
    {
        try
        {
            var table = BinaryData.U32(data, 0x64);
            if (table == 0) return false;
            var count = BinaryData.U32(data, table);
            BinaryData.Slice(data, table + 4L, count * 4L);
            for (var i = 0; i < count; i++)
            {
                var mesh = checked((long)table + BinaryData.U32(data, table + 4L + i * 4L));
                var sections = BinaryData.U32(data, mesh + 120);
                var start = checked(mesh + BinaryData.U32(data, mesh + 124));
                BinaryData.Slice(data, start, sections * 24L);
                for (var j = 0; j < sections; j++) if (BinaryData.U32(data, start + j * 24L + 16) > 0) return true;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException) { }
        return false;
    }

    private static HashSet<ulong> Fingerprint(PatchDocument document)
    {
        const ulong materialType = 0xeac0b497876adedf;
        var ids = document.Resources.Select(r => r.Record.FileId).Where(id => id != 0).ToHashSet();
        foreach (var entry in document.Resources)
        {
            var data = entry.Data.Span;
            if (entry.Record.TypeId == PatchTable.UnitTypeId && data.Length >= 0x80)
            {
                for (var offset = 0; offset < 0x28; offset += 8) ids.Add(BinaryData.U64(data, offset));
                var materials = BinaryData.U32(data, 0x70);
                if (materials > 0 && materials <= data.Length - 4)
                {
                    var count = BinaryData.U32(data, materials);
                    AddReferences(ids, data, materials + 4L + 4L * count, count);
                }
            }
            else if (entry.Record.TypeId == materialType && data.Length >= 136)
            {
                var count = BinaryData.U32(data, 0x40);
                AddReferences(ids, data, 136L + 4L * count, count);
            }
        }
        ids.Remove(0);
        return ids;
    }

    private static void AddReferences(HashSet<ulong> ids, ReadOnlySpan<byte> data, long offset, uint count)
    {
        if (offset > data.Length || count * 8L > data.Length - offset) return;
        for (var i = 0; i < count; i++) ids.Add(BinaryData.U64(data, offset + i * 8L));
    }

    private static Equipment[] Load()
    {
        JsonDocument Read(string name) => JsonDocument.Parse(typeof(EquipmentSafety).Assembly.GetManifestResourceStream($"Quartermaster.Repatcher.Data.{name}")!);
        using var armor = Read("armor_mappings.merged.json");
        using var helmet = Read("helmet_mappings.json");
        using var index = Read("archivehashes.json");
        var result = new List<Equipment>();
        foreach (var category in new[] { "Armor", "Helmet" })
            foreach (var archive in index.RootElement.GetProperty(category).EnumerateObject())
            {
                var name = archive.Value.GetString()!;
                var mappings = category == "Armor" ? armor : helmet;
                var parts = mappings.RootElement.TryGetProperty(name, out var mapping)
                    ? mapping.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetUInt64()) : [];
                result.Add(new(category, archive.Name, name, parts));
            }
        return result.ToArray();
    }
}
