using System.Buffers.Binary;
using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Repair;

/// <summary>Raw-preserving culling composition, based on hd2-mod-crates unit/culling.rs.</summary>
internal static class UnitCulling
{
    private sealed record Raw(uint Marker, byte[] Bytes);
    private sealed record Mesh(Raw Raw, int Stream, int Transform, int Bone, bool Culling);
    private sealed record Parsed(int StreamOffset, int MaterialsOffset, uint Trailer, Raw[] Streams, Mesh[] Meshes);
    private sealed record Transform(byte[] Local, byte[] Matrix, byte[] Entry, uint Hash);
    private sealed record Transforms(int Offset, int End, byte[] Header, Transform[] Records);
    private sealed record Bones(int Offset, int End, byte[][] Records);

    public static ResourcePayload Replace(ResourcePayload patch, ResourcePayload target)
    {
        var targetData = target.Data.ToArray();
        var targetUnit = Parse(targetData);
        var culling = targetUnit.Meshes.Where(m => m.Culling).ToArray();
        var targetBones = culling.Where(m => m.Bone >= 0).Select(m => m.Bone).Distinct().Order().ToArray();
        var boneRecords = targetBones.Length == 0 ? null : ReadBones(targetData);
        var transformIds = culling.Select(m => m.Transform).ToSortedSet();
        foreach (var index in targetBones)
            foreach (var id in BoneTransforms(At(boneRecords!.Records, index))) transformIds.Add(id);
        Transforms? transforms = null;
        if (transformIds.Count > 0)
        {
            transforms = ReadTransforms(targetData);
            var pending = new Stack<int>(transformIds);
            while (pending.TryPop(out var index))
            {
                var parent = Parent(At(transforms.Records, index));
                if (parent < transforms.Records.Length && transformIds.Add(parent)) pending.Push(parent);
            }
        }
        var data = patch.Data.ToArray();
        var transformMap = new Dictionary<int, int>();
        if (transforms is not null)
        {
            var source = ReadTransforms(data);
            var records = source.Records.ToList();
            var byHash = records.Select((r, i) => (r.Hash, i)).GroupBy(r => r.Hash).ToDictionary(g => g.Key, g => g.Last().i);
            var appended = new List<(int Target, int Output)>();
            foreach (var index in transformIds)
            {
                var record = At(transforms.Records, index);
                if (!byHash.TryGetValue(record.Hash, out var output))
                {
                    output = records.Count;
                    records.Add(record with { Entry = record.Entry.ToArray() });
                    byHash.Add(record.Hash, output);
                    appended.Add((index, output));
                }
                transformMap[index] = output;
            }
            foreach (var (index, output) in appended)
            {
                var parent = Parent(transforms.Records[index]);
                if (parent < transforms.Records.Length)
                    BinaryPrimitives.WriteUInt16LittleEndian(records[output].Entry.AsSpan(2), checked((ushort)transformMap[parent]));
            }
            if (records.Count != source.Records.Length)
            {
                using var replacement = new MemoryStream();
                replacement.Write(source.Header);
                foreach (var r in records) replacement.Write(r.Local);
                foreach (var r in records) replacement.Write(r.Matrix);
                foreach (var r in records) replacement.Write(r.Entry);
                foreach (var r in records) { var bytes = new byte[4]; BinaryData.Put32(bytes, 0, r.Hash); replacement.Write(bytes); }
                replacement.SetLength(Align(checked(source.Offset + (int)replacement.Length), 16) - source.Offset);
                var bytesOut = replacement.ToArray();
                BinaryData.Put32(bytesOut, 0, (uint)records.Count);
                data = Splice(data, source.Offset, source.End, bytesOut);
            }
        }
        var boneMap = new Dictionary<int, int>();
        if (boneRecords is not null)
        {
            var source = ReadBones(data);
            var records = source.Records.ToList();
            foreach (var index in targetBones)
            {
                var record = At(boneRecords.Records, index).ToArray();
                var indices = BoneTransforms(record);
                var offset = Int(record, 8);
                for (var i = 0; i < indices.Length; i++) BinaryData.Put32(record, checked(offset + i * 4), (uint)transformMap[indices[i]]);
                boneMap[index] = records.Count;
                records.Add(record);
            }
            using var replacement = new MemoryStream();
            replacement.SetLength(checked(4 + records.Count * 4));
            replacement.Position = replacement.Length;
            var offsets = new List<int>();
            foreach (var record in records) { offsets.Add((int)replacement.Position); replacement.Write(record); }
            replacement.SetLength(Align(checked(source.Offset + (int)replacement.Length), 16) - source.Offset);
            var bytes = replacement.ToArray();
            BinaryData.Put32(bytes, 0, (uint)records.Count);
            for (var i = 0; i < offsets.Count; i++) BinaryData.Put32(bytes, 4 + i * 4, (uint)offsets[i]);
            data = Splice(data, source.Offset, source.End, bytes);
        }
        var parsed = Parse(data);
        var streams = parsed.Streams.ToList();
        var streamMap = new Dictionary<int, int>();
        using var gpu = new MemoryStream();
        gpu.Write(patch.Gpu.Span);
        foreach (var index in culling.Select(m => m.Stream).Distinct().Order())
        {
            var raw = At(targetUnit.Streams, index);
            var bytes = raw.Bytes.ToArray();
            foreach (var field in new[] { 416, 424 })
            {
                var offset = Int(bytes, field);
                var size = Int(bytes, field + 4);
                var payload = BinaryData.Slice(target.Gpu.Span, offset, size);
                var next = Align(checked((int)gpu.Length), 16);
                gpu.SetLength(next); gpu.Position = next; gpu.Write(payload);
                BinaryData.Put32(bytes, field, (uint)next);
            }
            streamMap[index] = streams.Count;
            streams.Add(new(raw.Marker, bytes));
        }
        var meshes = parsed.Meshes.Where(m => !m.Culling).Select(m => m.Raw).ToList();
        foreach (var mesh in culling)
        {
            var bytes = mesh.Raw.Bytes.ToArray();
            BinaryData.Put32(bytes, 60, (uint)streamMap[mesh.Stream]);
            BinaryData.Put32(bytes, 48, (uint)transformMap[mesh.Transform]);
            if (mesh.Bone >= 0) BinaryData.Put32(bytes, 56, (uint)boneMap[mesh.Bone]);
            meshes.Add(new(mesh.Raw.Marker, bytes));
        }
        var streamTable = BuildRecords(streams, parsed.StreamOffset, parsed.Trailer);
        var meshOffset = checked(parsed.StreamOffset + streamTable.Length);
        var meshTable = BuildRecords(meshes, meshOffset, null);
        var combined = new byte[checked(streamTable.Length + meshTable.Length)];
        streamTable.CopyTo(combined, 0); meshTable.CopyTo(combined, streamTable.Length);
        data = Splice(data, parsed.StreamOffset, parsed.MaterialsOffset, combined);
        BinaryData.Put32(data, 0x5c, (uint)parsed.StreamOffset);
        BinaryData.Put32(data, 0x64, (uint)meshOffset);
        var ending = Required(data, 0x60);
        BinaryData.Slice(data, ending, 8);
        BinaryData.Put64(data, ending, (ulong)meshes.Count);
        var check = Parse(data);
        if (check.Meshes.Count(m => m.Culling) != culling.Length || check.Meshes.Length != meshes.Count)
            throw new InvalidDataException("Rebuilt unit failed culling-set validation.");
        return patch with { Data = data, Gpu = gpu.ToArray() };
    }

    private static Parsed Parse(byte[] data)
    {
        var stream = Required(data, 0x5c); var mesh = Required(data, 0x64); var materials = Required(data, 0x70);
        if (stream >= mesh || mesh >= materials || materials >= data.Length) throw new InvalidDataException("Unsupported unit table order.");
        var count = Int(data, materials);
        BinaryData.Slice(data, materials + 4L, count * 4L);
        var normal = Enumerable.Range(0, count).Select(i => BinaryData.U32(data, materials + 4L + i * 4L)).ToHashSet();
        var streams = ReadRecords(data, stream, mesh, 432, true, out var trailer);
        var meshes = ReadRecords(data, mesh, materials, 128, false, out _).Select(raw =>
        {
            var bytes = raw.Bytes;
            var n = Int(bytes, 104); var offset = Int(bytes, 108);
            BinaryData.Slice(bytes, offset, n * 4L);
            var slots = Enumerable.Range(0, n).Select(i => BinaryData.U32(bytes, offset + i * 4L)).ToArray();
            var sections = Int(bytes, 120); var start = Int(bytes, 124);
            BinaryData.Slice(bytes, start, sections * 24L);
            for (var i = 0; i < sections; i++) _ = At(slots, Int(bytes, checked(start + i * 24)));
            return new Mesh(raw, Int(bytes, 60), Int(bytes, 48), unchecked((int)BinaryData.U32(bytes, 56)),
                sections > 0 && slots.All(s => !normal.Contains(s)));
        }).ToArray();
        return new(stream, materials, trailer, streams, meshes);
    }

    private static Raw[] ReadRecords(byte[] data, int start, int end, int minimum, bool hasTrailer, out uint trailer)
    {
        var count = Int(data, start);
        var headerEnd = checked(start + 4 + count * 8 + (hasTrailer ? 4 : 0));
        BinaryData.Slice(data, start, headerEnd - (long)start);
        if (headerEnd > end) throw new InvalidDataException("Record table header overlaps data.");
        trailer = hasTrailer ? BinaryData.U32(data, headerEnd - 4) : 0;
        var offsets = Enumerable.Range(0, count).Select(i => checked(start + Int(data, checked(start + 4 + i * 4)))).ToArray();
        var records = new Raw[count];
        for (var i = 0; i < count; i++)
        {
            var next = i + 1 < count ? offsets[i + 1] : end;
            if (offsets[i] < headerEnd || next > end || next - (long)offsets[i] < minimum)
                throw new InvalidDataException("Invalid unit record range.");
            records[i] = new(BinaryData.U32(data, start + 4L + count * 4L + i * 4L), BinaryData.Slice(data, offsets[i], next - (long)offsets[i]).ToArray());
        }
        return records;
    }

    private static byte[] BuildRecords(IReadOnlyList<Raw> records, int absolute, uint? trailer)
    {
        var header = checked(4 + records.Count * 8 + (trailer.HasValue ? 4 : 0));
        using var output = new MemoryStream();
        output.SetLength(Align(checked(absolute + header), 16) - absolute);
        output.Position = output.Length;
        var offsets = new List<int>();
        foreach (var record in records) { offsets.Add((int)output.Position); output.Write(record.Bytes); }
        var bytes = output.ToArray();
        BinaryData.Put32(bytes, 0, (uint)records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            BinaryData.Put32(bytes, 4 + i * 4, (uint)offsets[i]);
            BinaryData.Put32(bytes, 4 + records.Count * 4 + i * 4, records[i].Marker);
        }
        if (trailer.HasValue) BinaryData.Put32(bytes, 4 + records.Count * 8, trailer.Value);
        return bytes;
    }

    private static Transforms ReadTransforms(byte[] data)
    {
        var offset = Required(data, 0x34); var count = Int(data, offset);
        var end = checked(offset + 16 + count * 136);
        BinaryData.Slice(data, offset, Align(end, 16) - (long)offset);
        var records = Enumerable.Range(0, count).Select(i => new Transform(
            data.AsSpan(offset + 16 + i * 64, 64).ToArray(),
            data.AsSpan(offset + 16 + count * 64 + i * 64, 64).ToArray(),
            data.AsSpan(offset + 16 + count * 128 + i * 4, 4).ToArray(),
            BinaryData.U32(data, offset + 16L + count * 132L + i * 4L))).ToArray();
        return new(offset, Align(end, 16), data.AsSpan(offset, 16).ToArray(), records);
    }

    private static Bones ReadBones(byte[] data)
    {
        var start = Required(data, 0x58); var end = Required(data, 0x5c);
        if (start >= end) throw new InvalidDataException("Invalid bone table order.");
        var count = Int(data, start);
        BinaryData.Slice(data, start + 4L, count * 4L);
        var headerEnd = checked(start + 4 + count * 4);
        var offsets = Enumerable.Range(0, count).Select(i => checked(start + Int(data, checked(start + 4 + i * 4)))).ToArray();
        var records = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var next = i + 1 < count ? offsets[i + 1] : end;
            if (offsets[i] < headerEnd || next > end || next < offsets[i]) throw new InvalidDataException("Invalid bone record range.");
            records[i] = BinaryData.Slice(data, offsets[i], next - (long)offsets[i]).ToArray();
        }
        return new(start, end, records);
    }

    private static int[] BoneTransforms(byte[] record)
    {
        var count = Int(record, 0); var offset = Int(record, 8);
        BinaryData.Slice(record, offset, count * 4L);
        return Enumerable.Range(0, count).Select(i => Int(record, checked(offset + i * 4))).ToArray();
    }

    private static byte[] Splice(byte[] data, int start, int end, byte[] replacement)
    {
        BinaryData.Slice(data, start, end - (long)start);
        var delta = checked(replacement.Length - (end - start));
        var output = new byte[checked(data.Length + delta)];
        data.AsSpan(0, start).CopyTo(output);
        replacement.CopyTo(output, start);
        data.AsSpan(end).CopyTo(output.AsSpan(start + replacement.Length));
        for (var field = 0x30; field <= 0x70; field += 4)
        {
            var offset = BinaryData.U32(output, field);
            if (offset >= end) BinaryData.Put32(output, field, checked((uint)(offset + (long)delta)));
            else if (offset > start && offset < end && field is not 0x64)
                throw new InvalidDataException("Unit offset points inside a replaced dependency table.");
        }
        return output;
    }

    private static int Parent(Transform record) => BinaryPrimitives.ReadUInt16LittleEndian(record.Entry.AsSpan(2));
    private static T At<T>(T[] values, int index) => index >= 0 && index < values.Length ? values[index] : throw new InvalidDataException("Missing unit dependency.");
    private static int Int(byte[] data, int field) => BinaryData.Length(BinaryData.U32(data, field));
    private static int Required(byte[] data, int field) => Int(data, field) is var offset && offset >= 0x74 ? offset : throw new InvalidDataException("Missing unit table.");
    private static int Align(int value, int alignment) => checked((value + alignment - 1) / alignment * alignment);
    private static SortedSet<int> ToSortedSet(this IEnumerable<int> values) => new(values);
}
