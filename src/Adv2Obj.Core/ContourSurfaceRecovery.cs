using System.Buffers.Binary;

namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private sealed record RoughContour(int Level, List<Vertex> Points, int Omitted);
    private sealed record RoughContours(int Count, double Step, List<RoughContour> Rings);

    // The primary rough record starts with a counted stack of XY contours.
    // These are independent measurements, not the polygon/triangle index table.
    // Keep this last in the recovery chain so established decoders are unchanged.
    private static Mesh? RecoverContourSurface(byte[] source, byte[] primary, CancellationToken token)
        => RecoverContourSurfaceWithFallback(source, primary, token, true);

    private static Mesh? RecoverContourSurfaceWithFallback(byte[] source, byte[] primary, CancellationToken token, bool improveAccuracy)
        => (improveAccuracy ? RecoverContourSurfaceCore(source, primary, token, true) : null)
            ?? RecoverContourSurfaceCore(source, primary, token, false);

    private static Mesh? RecoverContourSurfaceCore(byte[] source, byte[] primary, CancellationToken token, bool recoverHeaders)
    {
        RoughContours? contours = ReadRoughContours(primary, token, recoverHeaders);
        if (contours is null) return null;
        var rings = contours.Rings.ToDictionary(r => r.Level);
        var primaryRings = contours.Rings.ToDictionary(r => r.Level);
        int copied = 0;
        int cursor = checked((int)ReadUInt32(source, 0x34));
        bool skippedPrimary = false;
        while ((cursor = FindBytes(source, ZipSignature, cursor, source.Length)) >= 0)
        {
            token.ThrowIfCancellationRequested();
            int h = cursor++;
            if (h + 30 > source.Length || ReadUInt16(source, h + 8) != 8) continue;
            long size = ReadUInt32(source, h + 18), expanded = ReadUInt32(source, h + 22);
            long start = h + 30L + ReadUInt16(source, h + 26) + ReadUInt16(source, h + 28);
            if (size <= 0 || expanded < 50_000 || expanded > 64_000_000 || start + size > source.Length) continue;
            if (!skippedPrimary) { skippedPrimary = true; continue; }
            byte[]? bytes = InflateRestart(source.AsSpan((int)start, (int)size).ToArray(), 64_000_000);
            if (bytes is null) continue;
            RoughContours? other = ReadRoughContours(bytes, token, recoverHeaders);
            if (other is null || other.Count != contours.Count || other.Step != contours.Step) continue;
            // A later plan mesh cannot replace the rough. Require exact XY
            // agreement across most shared levels before accepting another copy.
            int shared = 0, confirmed = 0, firstConfirmed = contours.Count, lastConfirmed = -1;
            foreach (RoughContour ring in other.Rings)
            {
                if (!primaryRings.TryGetValue(ring.Level, out var original)) continue;
                shared++;
                var points = original.Points.ToHashSet();
                int matches = ring.Points.Count(points.Contains);
                // Saved contour copies can be simplified; compare their exact
                // common measurements and full XY extents, not point ordinals.
                double[] a = ContourBounds(original.Points), b = ContourBounds(ring.Points);
                double scale = Math.Sqrt(Math.Pow(a[2] - a[0], 2) + Math.Pow(a[3] - a[1], 2));
                if (matches >= Math.Max(3, Math.Min(ring.Points.Count, original.Points.Count) * .30)
                    && Enumerable.Range(0, 4).All(i => Math.Abs(a[i] - b[i]) <= scale * .02))
                {
                    confirmed++;
                    firstConfirmed = Math.Min(firstConfirmed, ring.Level);
                    lastConfirmed = Math.Max(lastConfirmed, ring.Level);
                }
            }
            System.Diagnostics.Trace.WriteLine($"Contour copy: {confirmed}/{shared} levels corroborated by exact points and matching extents.");
            if (shared < contours.Count / 4 || confirmed < shared * .90
                || lastConfirmed - firstConfirmed < contours.Count * .70) continue;
            foreach (RoughContour ring in other.Rings)
                if (rings.TryAdd(ring.Level, ring)) copied++;
        }
        return LoftRoughContours(contours with { Rings = rings.Values.OrderBy(r => r.Level).ToList() }, copied, token);
    }

    private static RoughContours? ReadRoughContours(byte[] bytes, CancellationToken token, bool recoverHeaders = true)
    {
        if (bytes.Length < 96 || ReadUInt32(bytes, 0) != 0) return null;
        int count = (int)ReadUInt32(bytes, 4), firstCount = (int)ReadUInt32(bytes, 20);
        if (count is < 16 or > 2048 || firstCount is < 3 or > 4096 || ReadDouble(bytes, 8) != 0
            || ReadUInt32(bytes, 16) != 1 || 24L + firstCount * 16L + 16 > bytes.Length) return null;
        int second = 24 + firstCount * 16;
        double step = ReadDouble(bytes, second);
        if (!double.IsFinite(step) || step <= 0 || step * (count - 1) > 10000) return null;
        int end = FindBytes(bytes, PolygonMeshSignature, second, bytes.Length);
        if (end < 0) end = bytes.Length; // A truncated primary may corroborate a complete later copy.
        var expected = new Dictionary<ulong, int>();
        for (int i = 1; i < count; i++)
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(step * i);
            for (int delta = -4; delta <= 4; delta++)
                expected.TryAdd(unchecked(bits + (ulong)delta) & 0x00ff_ffff_ffff_ffff, i);
        }
        var offsets = new Dictionary<int, List<int>> { [0] = [8] };
        for (int p = second; p + 16 <= end; p++)
        {
            if ((p & 4095) == 0) token.ThrowIfCancellationRequested();
            ulong bits = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(p, 8)) & 0x00ff_ffff_ffff_ffff;
            if (!expected.TryGetValue(bits, out int level)) continue;
            // Version and count share the same overwritten upper bytes in the
            // affected streams. This framing also excludes coordinate lookalikes.
            if (ReadUInt32(bytes, p + 8) == 0 || ReadUInt32(bytes, p + 12) == 0
                || (ReadUInt32(bytes, p + 8) >> 16) != (ReadUInt32(bytes, p + 12) >> 16)) continue;
            if (!offsets.TryGetValue(level, out var candidates)) offsets[level] = candidates = [];
            candidates.Add(p);
        }
        if (recoverHeaders) RecoverFramedContourHeaders(bytes, offsets, count, step, end, end < bytes.Length, token);
        var headers = offsets.Where(x => x.Value.Count == 1).Select(x => (Level: x.Key, Offset: x.Value[0]))
            .OrderBy(x => x.Level).ToList();
        if (headers.Count < count / 2 || headers[0].Level != 0 || headers[1] != (1, second)) return null;
        if (headers.Zip(headers.Skip(1)).Any(pair => pair.First.Offset >= pair.Second.Offset)) return null;
        var rings = new List<RoughContour>();
        var shortRecords = new List<(int Level, int Start, int End)>();
        for (int r = 0; r < headers.Count; r++)
        {
            var current = headers[r];
            var next = r + 1 < headers.Count ? headers[r + 1] : (Level: count, Offset: end);
            if (next.Level != current.Level + 1) continue;
            int length = next.Offset - current.Offset - 16;
            if (length < 48) continue;
            if (length % 16 != 0)
            {
                if (recoverHeaders) shortRecords.Add((current.Level, current.Offset, next.Offset));
                continue;
            }
            int n = length / 16;
            if (n > 4096 || (n & 255) != bytes[current.Offset + 12]) continue;
            var points = new List<Vertex>();
            int omitted = 0;
            for (int i = 0; i < n; i++)
            {
                int p = current.Offset + 16 + i * 16;
                var v = new Vertex(ReadDouble(bytes, p), ReadDouble(bytes, p + 8), step * current.Level);
                if (!IsPlausibleCoordinate(v.X) || !IsPlausibleCoordinate(v.Y)) { omitted++; continue; }
                if (points.Count == 0 || Distance(points[^1], v) > 1e-8) points.Add(v);
            }
            if (points.Count > 1 && Distance(points[0], points[^1]) < 1e-8) points.RemoveAt(points.Count - 1);
            if (points.Count < 3 || omitted > Math.Max(1, n / 100) || !IsSimpleRoughContour(points)) continue;
            rings.Add(new(current.Level, points, omitted));
        }
        // Plausible floating point values alone do not exclude overwritten
        // exponents. Bound whole rings against the distribution of measurements;
        // never pull an outlier into the shape by clamping its coordinates.
        if (rings.Count > 0)
        {
            double[] xs = rings.SelectMany(r => r.Points).Select(v => v.X).Order().ToArray();
            double[] ys = rings.SelectMany(r => r.Points).Select(v => v.Y).Order().ToArray();
            double x0 = xs[xs.Length / 100], x1 = xs[xs.Length * 99 / 100];
            double y0 = ys[ys.Length / 100], y1 = ys[ys.Length * 99 / 100];
            double margin = Math.Max(x1 - x0, y1 - y0) * .5;
            rings.RemoveAll(r => r.Points.Any(v => v.X < x0 - margin || v.X > x1 + margin || v.Y < y0 - margin || v.Y > y1 + margin));
        }
        if (recoverHeaders)
        {
            var guides = rings.ToDictionary(r => r.Level);
            foreach (var record in shortRecords)
            {
                token.ThrowIfCancellationRequested();
                if (!guides.TryGetValue(record.Level - 1, out var lower) || !guides.TryGetValue(record.Level + 1, out var upper)) continue;
                RoughContour? recovered = RecoverShortContour(bytes, record.Level, record.Start, record.End, step, lower, upper, token);
                if (recovered is not null) rings.Add(recovered);
            }
            rings.Sort((a, b) => a.Level.CompareTo(b.Level));
        }
        System.Diagnostics.Trace.WriteLine($"Contours: {rings.Count}/{count} usable rings; last {(rings.Count > 0 ? rings[^1].Level : -1)}.");
        return rings.Count < count / 2 ? null : new(count, step, rings);
    }

    private static void RecoverFramedContourHeaders(byte[] bytes, Dictionary<int, List<int>> offsets,
        int count, double step, int end, bool hasTerminalRecord, CancellationToken token)
    {
        // A single overwritten mantissa byte need not discard a whole scan
        // level. Recover its header only at a counted boundary from a neighbour,
        // with the remaining six height bytes and the version/count framing.
        // Conflicting candidates are kept ambiguous; coordinates are never edited.
        bool Framed(int p) => p >= 8 && p + 16 <= end && ReadUInt32(bytes, p + 8) != 0
            && ReadUInt32(bytes, p + 12) != 0
            && ReadUInt32(bytes, p + 8) >> 16 == ReadUInt32(bytes, p + 12) >> 16;
        bool HeightMatches(int p, int level)
        {
            if (p < 8 || p + 16 > end) return false;
            int allowedDifference = Framed(p) ? 1 : 0;
            ulong expected = (ulong)BitConverter.DoubleToInt64Bits(step * level);
            for (int delta = -4; delta <= 4; delta++)
            {
                ulong bits = unchecked(expected + (ulong)delta);
                int different = 0;
                for (int b = 0; b < 7; b++)
                    if (bytes[p + b] != (byte)(bits >> (b * 8))) different++;
                if (different <= allowedDifference) return true;
            }
            return false;
        }
        bool Known(int level, out int position)
        {
            position = -1;
            if (level == count && hasTerminalRecord) { position = end; return true; }
            if (!offsets.TryGetValue(level, out var candidates) || candidates.Count != 1) return false;
            position = candidates[0];
            return true;
        }
        for (int pass = 0; pass < count; pass++)
        {
            bool changed = false;
            for (int level = 2; level < count; level++)
            {
                token.ThrowIfCancellationRequested();
                if (offsets.ContainsKey(level)) continue;
                var candidates = new HashSet<int>();
                if (Known(level - 1, out int previous))
                {
                    for (int n = bytes[previous + 12]; n <= 4096; n += 256)
                    {
                        if (n < 3) continue;
                        int p = previous + 16 + n * 16;
                        if (HeightMatches(p, level)) candidates.Add(p);
                    }
                }
                if (Known(level + 1, out int next))
                {
                    for (int n = 3; n <= 4096 && next - 16 - n * 16 >= 8; n++)
                    {
                        int p = next - 16 - n * 16;
                        if (bytes[p + 12] == (n & 255) && HeightMatches(p, level)) candidates.Add(p);
                    }
                }
                // The candidate must remain between every established neighbour.
                int before = offsets.Where(x => x.Key < level && x.Value.Count == 1).Max(x => x.Value[0]);
                int after = offsets.Where(x => x.Key > level && x.Value.Count == 1).Select(x => x.Value[0]).DefaultIfEmpty(end).Min();
                candidates.RemoveWhere(p => p <= before || p >= after);
                if (candidates.Count == 0) continue;
                offsets[level] = candidates.Order().ToList();
                changed |= candidates.Count == 1;
            }
            if (!changed) break;
        }
    }

    private static double[] ContourBounds(List<Vertex> points) =>
        [points.Min(v => v.X), points.Min(v => v.Y), points.Max(v => v.X), points.Max(v => v.Y)];

    private static bool IsSimpleRoughContour(List<Vertex> points)
    {
        static double Turn(Vertex a, Vertex b, Vertex c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Vertex a = points[i], b = points[(i + 1) % points.Count];
            area += a.X * b.Y - a.Y * b.X;
            for (int j = i + 2; j < points.Count; j++)
            {
                if (i == 0 && j == points.Count - 1) continue;
                Vertex c = points[j], d = points[(j + 1) % points.Count];
                if (Math.Max(a.X, b.X) < Math.Min(c.X, d.X) || Math.Max(c.X, d.X) < Math.Min(a.X, b.X)
                    || Math.Max(a.Y, b.Y) < Math.Min(c.Y, d.Y) || Math.Max(c.Y, d.Y) < Math.Min(a.Y, b.Y)) continue;
                if (Turn(a, b, c) * Turn(a, b, d) < -1e-12 && Turn(c, d, a) * Turn(c, d, b) < -1e-12) return false;
            }
        }
        return double.IsFinite(area) && area > 1e-8;
    }

    private static Mesh? LoftRoughContours(RoughContours source, int copied, CancellationToken token)
    {
        var rings = source.Rings;
        if (rings.Count < source.Count * .70 || rings[0].Level != 0 || rings[^1].Level != source.Count - 1) return null;
        int maxGap = rings.Zip(rings.Skip(1)).Max(pair => pair.Second.Level - pair.First.Level);
        System.Diagnostics.Trace.WriteLine($"Contour loft: {rings.Count} rings; maximum gap {maxGap}.");
        if (maxGap > Math.Max(2, (int)Math.Ceiling(source.Count / 20.0))) return null;
        var vertices = rings.SelectMany(r => r.Points).ToList();
        var faces = new List<(int A, int B, int C)>();
        int start = 0;
        for (int r = 0; r + 1 < rings.Count; r++)
        {
            token.ThrowIfCancellationRequested();
            var a = rings[r].Points;
            var b = rings[r + 1].Points;
            int next = start + a.Count;
            faces.AddRange(TriangulateContourStrip(a, b, start, next, token));
            start = next;
        }
        var bottom = Enumerable.Range(0, rings[0].Points.Count).ToList();
        var top = Enumerable.Range(start, rings[^1].Points.Count).Reverse().ToList();
        var bottomFaces = TriangulatePlanarBoundary(bottom, vertices, out bool bottomClosed);
        var topFaces = TriangulatePlanarBoundary(top, vertices, out bool topClosed);
        if (!bottomClosed || !topClosed) { System.Diagnostics.Trace.WriteLine($"Contour caps: bottom={bottomClosed}, top={topClosed}."); return null; }
        faces.AddRange(bottomFaces); faces.AddRange(topFaces);
        try { ValidateClosedTopology(vertices.Count, faces); }
        catch (AdvFormatException e) { System.Diagnostics.Trace.WriteLine($"Contour topology: {e.Message}"); return null; }
        var mesh = new Mesh(vertices, faces, 0, []);
        double volume = ScanVolume(mesh);
        System.Diagnostics.Trace.WriteLine($"Contour volume: {volume:G17}.");
        if (!double.IsFinite(volume) || volume <= 0) return null;
        mesh.Warnings.Add($"Reconstructed the rough surface from {rings.Count}/{source.Count} stored contour levels and {rings.Sum(r => r.Points.Count):N0} measured points"
            + $" ({copied} levels recovered from a corroborated copy). {rings.Sum(r => r.Omitted)} unreadable points omitted; "
            + $"{source.Count - rings.Count} unreadable levels bridged; largest interval {maxGap * source.Step:G5}. "
            + "Triangle connectivity is reconstructed; accuracy across missing contours requires visual review.");
        return mesh with { UsesContourSurface = true };
    }

    private static List<(int A, int B, int C)> TriangulateContourStrip(List<Vertex> a, List<Vertex> b,
        int startA, int startB, CancellationToken token)
    {
        // Solve an ordered shortest strip, retaining every measured point.
        // Proportional perimeter matching can stretch triangles across corners
        // when neighbouring scans have different point counts and concavities.
        int anchorA = 0, anchorB = 0;
        double shortest = double.PositiveInfinity;
        for (int i = 0; i < a.Count; i++)
        for (int j = 0; j < b.Count; j++)
        {
            double distance = Distance(a[i], b[j]);
            if (distance < shortest) { shortest = distance; anchorA = i; anchorB = j; }
        }
        int width = b.Count + 1;
        var steps = new byte[(a.Count + 1) * width];
        var previous = Enumerable.Repeat(double.PositiveInfinity, width).ToArray();
        var current = new double[width];
        for (int i = 0; i <= a.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            for (int j = 0; j <= b.Count; j++)
            {
                if (i == 0 && j == 0) { current[j] = 0; continue; }
                if (i == a.Count && j == 0 || i == 0 && j == b.Count)
                { current[j] = double.PositiveInfinity; continue; }
                double left = j > 0 ? current[j - 1] : double.PositiveInfinity;
                double below = i > 0 ? previous[j] : double.PositiveInfinity;
                double distance = Distance(a[(anchorA + i) % a.Count], b[(anchorB + j) % b.Count]);
                current[j] = Math.Min(left, below) + distance * distance;
                steps[i * width + j] = (byte)(below <= left ? 0 : 1);
            }
            (previous, current) = (current, previous);
        }
        int ai = a.Count, bi = b.Count;
        int A(int index) => startA + (anchorA + index) % a.Count;
        int B(int index) => startB + (anchorB + index) % b.Count;
        var result = new List<(int A, int B, int C)>();
        while (ai > 0 || bi > 0)
        {
            if (steps[ai * width + bi] == 0)
            { result.Add((A(ai - 1), A(ai), B(bi))); ai--; }
            else
            { result.Add((A(ai), B(bi), B(bi - 1))); bi--; }
        }
        return result;
    }
}
