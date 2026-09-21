namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static List<GalaxyCandidate>? FindOrdinalGalaxyTable(byte[] source, List<GalaxyCandidate> candidates)
    {
        int cursor = Math.Max(0, source.Length - GalaxySearchWindow);
        while ((cursor = FindBytes(source, GalaxyTableSignature, cursor, source.Length)) >= 0)
        {
            int header = cursor++;
            if (header + 36 > source.Length) continue;
            uint length = ReadUInt32(source, header + 16);
            if (length is < 100 or > 500_000 || header + 20L + length > source.Length) continue;
            int end = header + 20 + (int)length;
            foreach (int countOffset in new[] { 28, 30, 32 })
            {
                uint count = ReadUInt32(source, header + countOffset);
                if (count is < 3 or > 7) continue;
                int first = header + countOffset + 4;
                // Ordinals identify the CSV labels. Some tables reuse a code;
                // accept them only when the bounded record has exactly one of
                // every ordinal, with matching duplicate coordinate blocks.
                var table = candidates.Where(c => c.Offset >= first && c.Offset + 64 <= end
                    && c.Ordinal < count && ReadUInt32(source, c.Offset + 8) == 1
                    && ReadUInt32(source, c.Offset + 12) == 0
                    && source.AsSpan(c.Offset + 16, 24).SequenceEqual(source.AsSpan(c.Offset + 40, 24))).ToList();
                if (table.Count != count || table.Min(c => c.Offset) != first
                    || table.Select(c => c.Ordinal).Distinct().Count() != count) continue;
                // Coordinates are unchanged; only the redundant code field
                // is ignored in favor of the distinct stored label ordinal.
                return table;
            }
        }
        return null;
    }
}
