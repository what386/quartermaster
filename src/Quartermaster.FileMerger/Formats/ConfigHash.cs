using System.Buffers.Binary;

namespace Quartermaster.FileMerger;

/// <summary>The game's seeded MurmurHash64A, including the inverse used for a hash-preserving trailer.</summary>
public static class ConfigHash
{
    private const ulong Multiplier = 0xc6a4a7935bd1e995;
    private const ulong Inverse = 0x5f7a0ea7e59b19bd;
    private const ulong Seed = 16045690983977655786;
    private static ulong Mix(ulong word) { unchecked { word *= Multiplier; word ^= word >> 47; return word * Multiplier; } }
    private static ulong Unmix(ulong word) { unchecked { word *= Inverse; word ^= word >> 47; return word * Inverse; } }

    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            var state = Seed ^ ((ulong)data.Length * Multiplier);
            var whole = data.Length & ~7;
            for (var i = 0; i < whole; i += 8) state = (state ^ Mix(BinaryPrimitives.ReadUInt64LittleEndian(data[i..]))) * Multiplier;
            ulong tail = 0;
            for (var i = data.Length - 1; i >= whole; i--) tail = (tail << 8) | data[i];
            if (whole != data.Length) state = (state ^ tail) * Multiplier;
            state ^= state >> 47; state *= Multiplier; return state ^ (state >> 47);
        }
    }

    // File mods extend the final LDLD payload with a correction trailer. Solve its last aligned word directly,
    // leaving all gameplay fields untouched; no salt search or approximate float changes are needed.
    internal static byte[] Correct(ConfigDocument document, byte[] edited, ulong target)
    {
        if (Compute(edited) == target) return edited;
        var output = new byte[checked(((edited.Length + 7) & ~7) + 8)];
        edited.CopyTo(output, 0);
        var last = document.Blocks[^1];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(last.Header + 12), checked((uint)(output.Length - last.Data)));
        unchecked
        {
            var state = Seed ^ ((ulong)output.Length * Multiplier);
            for (var i = 0; i < output.Length - 8; i += 8)
                state = (state ^ Mix(BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(i)))) * Multiplier;
            var final = target ^ (target >> 47);
            final *= Inverse; final ^= final >> 47;
            var word = Unmix((final * Inverse) ^ state);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(output.Length - 8), word);
        }
        if (Compute(output) != target) throw new InvalidDataException("Merged config hash correction failed.");
        ConfigDocument.Parse(output);
        return output;
    }
}
