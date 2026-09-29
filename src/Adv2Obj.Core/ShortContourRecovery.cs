namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static RoughContour? RecoverShortContour(byte[] bytes, int level, int header, int end, double step,
        RoughContour lower, RoughContour upper, CancellationToken token)
    {
        int start = header + 16, length = end - start;
        int n = (length + 15) / 16;
        while (n <= length / 16 + 2 && (n & 255) != bytes[header + 12]) n++;
        int missing = n * 16 - length;
        if (n is < 64 or > 4096 || missing is < 1 or > 32 || (n & 255) != bytes[header + 12]) return null;
        // Decode independently from each known boundary. Only intact byte
        // pairs common to every supported splice survive; never interpolate XY.
        int lost = (missing + 15) / 16 + 1;
        var prefix = new Vertex[n];
        var suffix = new Vertex[n];
        var goodPrefix = new bool[n];
        var goodSuffix = new bool[n];
        bool NearGuides(Vertex v) => IsPlausibleCoordinate(v.X) && IsPlausibleCoordinate(v.Y)
            && DistanceToContour(v, lower.Points) <= 3 * step && DistanceToContour(v, upper.Points) <= 3 * step;
        for (int i = 0; i < n; i++)
        {
            token.ThrowIfCancellationRequested();
            int a = start + i * 16, b = end - (n - i) * 16;
            if (a + 16 <= end)
            {
                prefix[i] = new(ReadDouble(bytes, a), ReadDouble(bytes, a + 8), step * level);
                goodPrefix[i] = NearGuides(prefix[i]);
            }
            if (b >= start)
            {
                suffix[i] = new(ReadDouble(bytes, b), ReadDouble(bytes, b + 8), step * level);
                goodSuffix[i] = NearGuides(suffix[i]);
            }
        }
        int prefixEnd = Array.FindIndex(goodPrefix, valid => !valid);
        if (prefixEnd < 0) prefixEnd = n;
        int suffixStart = Array.FindLastIndex(goodSuffix, valid => !valid) + 1;
        var supported = new List<int>();
        for (int split = 3; split + lost <= n - 3; split++)
        {
            if (split > prefixEnd || split + lost < suffixStart) continue;
            var candidate = prefix.Take(split).Concat(suffix.Skip(split + lost)).ToList();
            if (IsSimpleRoughContour(candidate)) supported.Add(split);
        }
        if (supported.Count == 0) return null;
        int keepPrefix = supported.Min(), keepSuffixFrom = supported.Max() + lost;
        int omitted = keepSuffixFrom - keepPrefix;
        if (omitted > Math.Max(3, n / 20)) return null;
        var points = prefix.Take(keepPrefix).Concat(suffix.Skip(keepSuffixFrom)).ToList();
        if (!IsSimpleRoughContour(points)) return null;
        return new RoughContour(level, points, omitted);
    }

    private static double DistanceToContour(Vertex v, List<Vertex> contour)
    {
        double best = double.PositiveInfinity;
        for (int i = 0; i < contour.Count; i++)
        {
            Vertex a = contour[i], b = contour[(i + 1) % contour.Count];
            double x = b.X - a.X, y = b.Y - a.Y, denominator = x * x + y * y;
            double t = denominator > 0 ? Math.Clamp(((v.X - a.X) * x + (v.Y - a.Y) * y) / denominator, 0, 1) : 0;
            double dx = v.X - (a.X + t * x), dy = v.Y - (a.Y + t * y);
            best = Math.Min(best, dx * dx + dy * dy);
        }
        return Math.Sqrt(best);
    }
}
