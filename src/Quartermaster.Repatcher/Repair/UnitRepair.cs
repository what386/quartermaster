using Quartermaster.Repatcher.Formats;

namespace Quartermaster.Repatcher.Repair;

internal static class UnitRepair
{
    public static byte[] Repair(ReadOnlySpan<byte> mod, ReadOnlySpan<byte> original)
    {
        BinaryData.Slice(mod, 0, 0x74);
        BinaryData.Slice(original, 0, 0x74);
        var lodOffset = BinaryData.U32(mod, 0x30);
        var jointOffset = BinaryData.U32(mod, 0x34);
        var gameLodOffset = BinaryData.U32(original, 0x30);
        var gameJointOffset = BinaryData.U32(original, 0x34);
        if (lodOffset < 0x74 || jointOffset < lodOffset || gameLodOffset < 0x74 || gameJointOffset < gameLodOffset)
            throw new InvalidDataException("Invalid unit LOD offsets.");
        BinaryData.Slice(mod, lodOffset, (long)jointOffset - lodOffset);
        var lod = BinaryData.Slice(original, gameLodOffset, (long)gameJointOffset - gameLodOffset);
        var working = mod.ToArray();
        if (BinaryData.U32(mod, 0x2c) < 0xA4CD36)
        {
            var layoutList = BinaryData.U32(mod, 0x5c);
            if (layoutList != 0)
            {
                var count = BinaryData.U32(mod, layoutList);
                var offsets = BinaryData.Slice(mod, (long)layoutList + 4, 4L * count);
                for (var i = 0; i < count; i++)
                {
                    var start = (long)layoutList + BinaryData.U32(offsets, i * 4L) + 8;
                    BinaryData.Slice(mod, start, 16 * 20);
                    for (var j = 0; j < 16; j++)
                    {
                        var formatOffset = (int)start + j * 20 + 4;
                        var format = BinaryData.U32(mod, formatOffset);
                        if (format > 16) BinaryData.Put32(working, formatOffset, checked(format + 4));
                    }
                }
            }
        }
        var delta = lod.Length - ((long)jointOffset - lodOffset);
        var length = BinaryData.Length(checked((ulong)(mod.Length + delta)));
        var result = new byte[length];
        working.AsSpan(0, (int)lodOffset).CopyTo(result);
        lod.CopyTo(result.AsSpan((int)lodOffset));
        working.AsSpan((int)jointOffset).CopyTo(result.AsSpan((int)lodOffset + lod.Length));
        BinaryData.Put32(result, 0x2c, BinaryData.U32(original, 0x2c));
        for (var i = 0; i < 16; i++)
        {
            var field = 0x34 + i * 4;
            var offset = BinaryData.U32(mod, field);
            if (offset == 0) continue;
            if (offset > mod.Length || (offset > lodOffset && offset < jointOffset))
                throw new InvalidDataException("Unit offset points outside data or inside the replaced LOD block.");
            if (offset > lodOffset) BinaryData.Put32(result, field, checked((uint)(offset + delta)));
        }
        return result;
    }
}
