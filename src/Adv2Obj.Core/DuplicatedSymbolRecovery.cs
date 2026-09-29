namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static List<GalaxyCandidate>? FindVerifiedInactiveGalaxyTable(byte[] source, List<GalaxyCandidate> candidates)
    {
        int cursor = GalaxySearchStart(source);
        while ((cursor = FindBytes(source, GalaxyTableSignature, cursor, source.Length)) >= 0)
        {
            int header = cursor++;
            foreach (int offset in new[] { 28, 32 })
            {
                if (header + offset + 84 > source.Length) continue;
                uint count = ReadUInt32(source, header + offset);
                int first = header + offset + 4;
                uint inactiveCode = ReadUInt32(source, first), inactiveOrdinal = ReadUInt32(source, first + 4);
                if (count is < 4 or > 7 || inactiveCode >= count || inactiveOrdinal >= count
                    || ReadUInt32(source, first + 8) != 2 || ReadUInt32(source, first + 12) is not (3 or 4)
                    || source.AsSpan(first + 16, 48).IndexOfAnyExcept((byte)0) >= 0) continue;
                int next = FindBytes(source, GalaxyTableSignature, first + 80, source.Length);
                int end = Math.Min(source.Length, first + 500_000);
                if (next >= 0) end = Math.Min(end, next);
                var entries = candidates.Where(c => c.Offset >= first + 80 && c.Offset + 64 <= end
                    && c.Code < count && c.Code != inactiveCode && c.Ordinal < count && c.Ordinal != inactiveOrdinal
                    && ReadUInt32(source, c.Offset + 8) == 1 && ReadUInt32(source, c.Offset + 12) == 0
                    && source.AsSpan(c.Offset + 16, 24).SequenceEqual(source.AsSpan(c.Offset + 40, 24)))
                    .Select(c => c with { Position = ReadVertex(source, c.Offset + 16), Repaired = false }).ToList();
                if (entries.Count == count - 1 && entries.Select(c => c.Code).Distinct().Count() == count - 1
                    && entries.Select(c => c.Ordinal).Distinct().Count() == count - 1 && entries.All(c => IsGalaxyPosition(c.Position))) return entries;
            }
        }
        return null;
    }

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
