namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static Vertex? RecoverShortSymbolHeader(byte[] source, int record)
    {
        for (int missing = 1; missing <= 3; missing++)
        {
            int position = record + 16 - missing;
            if (source.AsSpan(record + 12, 4 - missing).IndexOfAnyExcept((byte)0) >= 0
                || !source.AsSpan(position, 24).SequenceEqual(source.AsSpan(position + 24, 24))) continue;
            Vertex value = ReadVertex(source, position);
            if (IsGalaxyPosition(value)) return value;
        }
        return null;
    }

    private static Vertex? RecoverDuplicatedSymbolPosition(byte[] source, int record)
    {
        // Variable-length symbol records duplicate XYZ. A short first copy may
        // lose bytes, while the complete second copy retains exact coordinates.
        for (int missing = 1; missing <= 4; missing++)
        {
            int second = record + 40 - missing;
            Vertex value = ReadVertex(source, second);
            if (!IsGalaxyPosition(value)) continue;
            ReadOnlySpan<byte> shortCopy = source.AsSpan(record + 16, 24 - missing);
            ReadOnlySpan<byte> complete = source.AsSpan(second, 24);
            int matched = 0;
            foreach (byte item in complete)
                if (matched < shortCopy.Length && item == shortCopy[matched]) matched++;
            if (matched == shortCopy.Length) return value;
        }
        return null;
    }
}
