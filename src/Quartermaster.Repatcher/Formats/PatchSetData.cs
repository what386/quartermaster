namespace Quartermaster.Repatcher.Formats;

/// <summary>A main patch and its optional companions. Null and an empty companion are distinct.</summary>
public sealed record PatchSetData(ReadOnlyMemory<byte> Main, ReadOnlyMemory<byte>? Gpu = null,
    ReadOnlyMemory<byte>? Stream = null);

public sealed record ResourcePayload(ResourceRecord Record, ReadOnlyMemory<byte> Data,
    ReadOnlyMemory<byte> Gpu, ReadOnlyMemory<byte> Stream);

/// <summary>Checked resource views and a serializer for complete patch sets.</summary>
public sealed class PatchDocument
{
    private readonly PatchSetData source;
    public PatchTable Table { get; }
    public IReadOnlyList<ResourcePayload> Resources { get; }

    private PatchDocument(PatchSetData source, PatchTable table, ResourcePayload[] resources)
        => (this.source, Table, Resources) = (source, table, Array.AsReadOnly(resources));

    public static PatchDocument Read(PatchSetData source, bool validateCompanions = true)
    {
        var table = PatchTable.Read(source.Main.Span);
        var keys = new HashSet<(ulong, ulong)>();
        foreach (var record in table.Resources)
            if (!keys.Add((record.FileId, record.TypeId)))
                throw new InvalidDataException($"Duplicate resource {record.FileId:x16}/{record.TypeId:x16}.");
        ulong end = (ulong)table.DataStart;
        foreach (var record in table.Resources.Where(r => r.DataSize > 0).OrderBy(r => r.DataOffset))
        {
            if (record.DataOffset < end) throw new InvalidDataException("Overlapping resource data or resource inside TOC.");
            BinaryData.Slice(source.Main.Span, BinaryData.Length(record.DataOffset), record.DataSize);
            end = checked(record.DataOffset + record.DataSize);
        }
        var resources = table.Resources.Select(r => new ResourcePayload(r,
            Part(source.Main, r.DataOffset, r.DataSize),
            validateCompanions ? Part(source.Gpu ?? ReadOnlyMemory<byte>.Empty, r.GpuOffset, r.GpuSize) : ReadOnlyMemory<byte>.Empty,
            validateCompanions ? Part(source.Stream ?? ReadOnlyMemory<byte>.Empty, r.StreamOffset, r.StreamSize) : ReadOnlyMemory<byte>.Empty)).ToArray();
        return new(source, table, resources);
    }

    private static ReadOnlyMemory<byte> Part(ReadOnlyMemory<byte> bytes, ulong offset, uint size)
    {
        if (size == 0) return ReadOnlyMemory<byte>.Empty;
        var start = BinaryData.Length(offset);
        BinaryData.Slice(bytes.Span, start, size);
        return bytes.Slice(start, BinaryData.Length(size));
    }

    /// <summary>Rebuilds by resource identity, preserving header/type metadata and payload bytes.</summary>
    public PatchSetData Rebuild(IReadOnlyList<ResourcePayload> resources, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var main = new MemoryStream();
        using var gpu = new MemoryStream();
        using var stream = new MemoryStream();
        var headerSize = checked(72 + Table.Types.Count * 32 + resources.Count * 80);
        main.Write(source.Main.Span[..Table.ItemTableOffset]);
        main.SetLength(headerSize);
        main.Position = headerSize;
        var rows = new List<ResourceRecord>();
        // Grouping matches the type table; body positions are never inferred from old row order.
        foreach (var type in Table.Types)
            foreach (var resource in resources.Where(r => r.Record.TypeId == type.TypeId))
            {
                ct.ThrowIfCancellationRequested();
                rows.Add(resource.Record with
                {
                    DataOffset = (ulong)main.Position, DataSize = checked((uint)resource.Data.Length),
                    GpuOffset = Append(gpu, resource.Gpu, 64), GpuSize = checked((uint)resource.Gpu.Length),
                    StreamOffset = Append(stream, resource.Stream, 64), StreamSize = checked((uint)resource.Stream.Length),
                    EntryIndex = checked((uint)rows.Count + 1)
                });
                main.Write(resource.Data.Span);
            }
        if (rows.Count != resources.Count) throw new InvalidDataException("Rebuild contains an unknown resource type.");
        if (main.Length < 256L * rows.Count) main.SetLength(checked(256L * rows.Count));
        var bytes = main.ToArray();
        BinaryData.Put32(bytes, 8, checked((uint)rows.Count));
        for (var i = 0; i < Table.Types.Count; i++)
            BinaryData.Put64(bytes, 72 + i * 32 + 16, (ulong)rows.Count(r => r.TypeId == Table.Types[i].TypeId));
        for (var i = 0; i < rows.Count; i++) rows[i].Write(bytes.AsSpan(Table.ItemTableOffset + i * 80, 80));
        var result = new PatchSetData(bytes,
            source.Gpu.HasValue || gpu.Length > 0 ? new ReadOnlyMemory<byte>(gpu.ToArray()) : (ReadOnlyMemory<byte>?)null,
            source.Stream.HasValue || stream.Length > 0 ? new ReadOnlyMemory<byte>(stream.ToArray()) : (ReadOnlyMemory<byte>?)null);
        Read(result);
        return result;
    }

    private static ulong Append(MemoryStream output, ReadOnlyMemory<byte> bytes, int alignment)
    {
        if (bytes.Length == 0) return 0;
        var offset = checked((output.Length + alignment - 1) / alignment * alignment);
        output.SetLength(offset);
        output.Position = offset;
        output.Write(bytes.Span);
        return (ulong)offset;
    }
}
