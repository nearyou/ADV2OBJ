namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    // Damaged dictionary references can overwrite upper index bytes in both
    // copies. Resolve the surviving low bytes against source supporting planes,
    // rather than treating a truncated index as the original vertex number.
    private static Mesh? TryRecoverPairedPolygonSurface(byte[] payload, CancellationToken token)
    {
        int cursor = 0;
        while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, payload.Length)) >= 0)
        {
            int first = cursor++;
            int second = FindBytes(payload, PolygonMeshSignature, first + 28, payload.Length);
            if (second < 0) continue;
            int length = second - first - 24;
            if (length < 16 || (length - 16) % 38 != 0) continue;
            int count = (length - 16) / 38, dense = second + second - first + 1;
            if (count is < 8 or > 2048 || (count & 1) != 0 || dense + 28 > payload.Length) continue;
            bool duplicate = payload.AsSpan(first + 28, count * 24).SequenceEqual(payload.AsSpan(second + 28, count * 24));
            if (!duplicate && ReadUInt32(payload, first + 20) != length) continue;
            int table = first + 28 + count * 24, end = second;
            var raw = new List<uint[]>();
            int p = table + 4;
            for (int f = 0; f < count / 2 + 2 && p + 4 <= end; f++)
            {
                int sides = payload[p];
                if (sides is < 3 or > 64 || p + 4 + sides * 4 > end) break;
                raw.Add(Enumerable.Range(0, sides).Select(i => ReadUInt32(payload, p + 4 + i * 4)).ToArray());
                p += 4 + sides * 4;
            }
            bool unframed = raw.Count != count / 2 + 2 || p != end;
            if (unframed)
            {
                raw.Clear();
                // A damaged polygon size need not discard every later plane.
                // Search the bounded table; geometry below must identify at
                // least 95% of the distinct declared surface planes.
                for (int q = table + 4; q + 16 <= end; q += 4)
                {
                    int sides = payload[q];
                    if (sides is < 3 or > 64 || q + 4 + sides * 4 > end) continue;
                    raw.Add(Enumerable.Range(0, sides).Select(i => ReadUInt32(payload, q + 4 + i * 4)).ToArray());
                }
                if (raw.Count < count / 2) continue;
            }
            var vertices = Enumerable.Range(0, count).Select(i => ReadVertex(payload, first + 28 + i * 24)).ToList();
            var invalid = Enumerable.Range(0, count).SelectMany(i => Enumerable.Range(0, 3)
                .Where(a => !IsPlausibleCoordinate(Coordinate(vertices[i], a))).Select(a => (Index: i, Axis: a))).ToList();
            if (invalid.Count > count * .10 || invalid.Count > count * .05
                && invalid.Select(v => (v.Axis, (ulong)BitConverter.DoubleToInt64Bits(Coordinate(vertices[v.Index], v.Axis)) >> 56)).Distinct().Count() > 1) continue;
            uint overwrittenVersion = ReadUInt32(payload, first + 16);
            var candidates = raw.Select(face => face.Select(word => word < count ? [(int)word]
                : Enumerable.Range(0, count).Where(i => (i & 255) == (word & 255)
                    || i == 1 && word == overwrittenVersion).ToArray()).ToArray()).ToArray();
            int planeRepairs = RecoverRepeatedPairedCoordinates(vertices, candidates, token);
            planeRepairs += RecoverPairedCoordinatePlanes(vertices, candidates, token);
            invalid = Enumerable.Range(0, count).SelectMany(i => Enumerable.Range(0, 3)
                .Where(a => !IsPlausibleCoordinate(Coordinate(vertices[i], a))).Select(a => (Index: i, Axis: a))).ToList();
            int damagedAxis = invalid.GroupBy(v => v.Axis).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            Mesh? selected = null;
            // Only an overwritten exponent byte is considered; all surviving
            // mantissa bits stay exact. Both signs must be tested for ambiguity.
            var variants = new List<(byte Prefix, int[] Reflect)>();
            if (invalid.Count > 0) variants.AddRange(new[] { ((byte)0x40, Array.Empty<int>()), ((byte)0xc0, Array.Empty<int>()) });
            else
            {
                variants.Add((0, []));
                // A repeated cap sign is accepted only if all surface checks
                // below uniquely support the reflected candidate.
                for (int axis = 0; axis < 3; axis++)
                    foreach (var group in vertices.Select((v, i) => (Value: Coordinate(v, axis), Index: i)).GroupBy(v => v.Value)
                        .Where(g => g.Key != 0 && g.Count() >= 3 && g.Count() <= count * .05))
                        variants.Add(((byte)(axis + 1), group.Select(v => v.Index).ToArray()));
            }
            foreach (var (prefix, reflect) in variants)
            {
                token.ThrowIfCancellationRequested();
                var points = vertices.ToList();
                foreach (int index in reflect) points[index] = SetCoordinate(points[index], prefix - 1, -Coordinate(points[index], prefix - 1));
                foreach (var item in invalid)
                {
                    if (item.Axis != damagedAxis) continue;
                    ulong bits = (ulong)BitConverter.DoubleToInt64Bits(Coordinate(points[item.Index], item.Axis));
                    points[item.Index] = SetCoordinate(points[item.Index], item.Axis,
                        BitConverter.Int64BitsToDouble((long)((bits & 0x00ff_ffff_ffff_ffff) | ((ulong)prefix << 56))));
                }
                int additionalRepairs = RecoverPairedCoordinatePlanes(points, candidates, token);
                if (points.Any(v => !IsPlausible(v))) continue;
                try
                {
                    Mesh hull = BuildConvexHull(new(points, [], 0, []), token);
                    System.Diagnostics.Trace.WriteLine($"Paired polygon hull: {hull.Vertices.Count}/{count}, exponent {prefix:x2}.");
                    if (hull.Vertices.Count != count) continue;
                    var planes = hull.Faces.Select(f =>
                    {
                        Vertex a = hull.Vertices[f.A], n = Cross(Subtract(hull.Vertices[f.B], a), Subtract(hull.Vertices[f.C], a));
                        n = Multiply(n, 1 / Length(n));
                        return (Normal: n, Offset: Dot(n, a));
                    }).GroupBy(x => (Math.Round(x.Normal.X, 7), Math.Round(x.Normal.Y, 7), Math.Round(x.Normal.Z, 7), Math.Round(x.Offset, 4)))
                        .Select(g => g.First()).ToArray();
                    var polygons = new List<int[]>();
                    int matchedIndices = 0;
                    foreach (var face in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        var matches = new List<(int[] Ids, int Matched)>();
                        foreach (var plane in planes)
                        {
                            int[] members = Enumerable.Range(0, count).Where(i => Math.Abs(Dot(plane.Normal, points[i]) - plane.Offset) < .01).ToArray();
                            if (members.Length != face.Length) continue;
                            int[][] onPlane = face.Select(ids => ids.Intersect(members).ToArray()).ToArray();
                            int matched = onPlane.Where(ids => ids.Length == 1).Select(ids => ids[0]).Distinct().Count();
                            if (matched < Math.Max(3, (int)Math.Ceiling(face.Length * .75))) continue;
                            if (!matches.Any(m => m.Ids.SequenceEqual(members))) matches.Add((members, matched));
                        }
                        if (matches.Count != 1) continue;
                        if (unframed && polygons.Any(ids => ids.SequenceEqual(matches[0].Ids))) continue;
                        polygons.Add(matches[0].Ids);
                        matchedIndices += matches[0].Matched;
                    }
                    System.Diagnostics.Trace.WriteLine($"Paired planes: {polygons.Count}/{raw.Count}; indices {matchedIndices}/{raw.Sum(f => f.Length)}.");
                    int expectedPlanes = count / 2 + 2;
                    if (polygons.Count < expectedPlanes * .95 || matchedIndices < count * 3 * .90
                        || polygons.Select(f => string.Join(",", f)).Distinct().Count() != polygons.Count
                        || polygons.SelectMany(f => f).GroupBy(i => i).Any(g => g.Count() > 3)) continue;
                    ValidateClosedTopology(hull.Vertices.Count, hull.Faces);
                    if (selected is not null)
                    {
                        if (selected.Vertices.Zip(hull.Vertices).All(pair => Distance(pair.First, pair.Second) < 1e-7)) continue;
                        selected = null; break;
                    }
                    int coordinateRepairs = invalid.Count(v => v.Axis == damagedAxis) + planeRepairs + reflect.Length + additionalRepairs;
                    selected = new Mesh(hull.Vertices, hull.Faces, coordinateRepairs,
                        [$"Recovered the {(duplicate ? "duplicated" : "independently bounded")} polygon surface ({count} vertices; {polygons.Count}/{expectedPlanes} source planes verified). Repaired {coordinateRepairs} coordinates and damaged polygon indices. Coarser than the dense scan; requires visual review."])
                        { UsesCoarseSurface = true };
                }
                catch (AdvFormatException) { }
            }
            if (selected is not null) return selected;
        }
        return null;
    }

    private static int RecoverPairedCoordinatePlanes(List<Vertex> vertices, int[][][] polygons, CancellationToken token)
    {
        int repaired = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            var valid = vertices.Where(IsPlausible).ToList();
            if (valid.Count == vertices.Count || valid.Count < vertices.Count * .90) break;
            Mesh hull;
            try { hull = BuildConvexHull(new(valid, [], 0, []), token); }
            catch (AdvFormatException) { break; }
            var planes = hull.Faces.Select(f =>
            {
                Vertex a = hull.Vertices[f.A], n = Cross(Subtract(hull.Vertices[f.B], a), Subtract(hull.Vertices[f.C], a));
                n = Multiply(n, 1 / Length(n));
                return (Normal: n, Offset: Dot(n, a));
            }).GroupBy(x => (Math.Round(x.Normal.X, 7), Math.Round(x.Normal.Y, 7), Math.Round(x.Normal.Z, 7), Math.Round(x.Offset, 4)))
                .Select(g => g.First()).ToArray();
            var evidence = new Dictionary<int, List<double>>();
            var supported = planes.Where(plane => polygons.Any(face => face.Count(ids => ids.Any(i => IsPlausible(vertices[i])
                && Math.Abs(Dot(plane.Normal, vertices[i]) - plane.Offset) < .01)) >= Math.Max(3, (int)Math.Ceiling(face.Length * .75)))).ToArray();
            foreach (var face in polygons)
            {
                var matches = planes.Where(plane => face.Count(ids => ids.Any(i => IsPlausible(vertices[i])
                    && Math.Abs(Dot(plane.Normal, vertices[i]) - plane.Offset) < .01)) >= Math.Max(3, face.Length - 2)).ToList();
                if (matches.Count != 1) continue;
                var plane = matches[0];
                foreach (int i in face.SelectMany(x => x).Distinct())
                {
                    int[] missing = Enumerable.Range(0, 3).Where(a => !IsPlausibleCoordinate(Coordinate(vertices[i], a))).ToArray();
                    if (missing.Length != 1 || Math.Abs(Coordinate(plane.Normal, missing[0])) < 1e-6) continue;
                    double known = Enumerable.Range(0, 3).Where(a => a != missing[0]).Sum(a => Coordinate(vertices[i], a) * Coordinate(plane.Normal, a));
                    double value = (plane.Offset - known) / Coordinate(plane.Normal, missing[0]);
                    if (!IsPlausibleCoordinate(value)) continue;
                    if (!evidence.TryGetValue(i, out var values)) evidence[i] = values = [];
                    values.Add(value);
                }
            }
            foreach (var (i, values) in evidence)
            {
                if (values.Count < 2 || values.Max() - values.Min() > .01) continue;
                int axis = Enumerable.Range(0, 3).Single(a => !IsPlausibleCoordinate(Coordinate(vertices[i], a)));
                vertices[i] = SetCoordinate(vertices[i], axis, values.Average()); repaired++;
            }
            for (int i = 0; i < vertices.Count; i++)
            {
                int[] missing = Enumerable.Range(0, 3).Where(a => !IsPlausibleCoordinate(Coordinate(vertices[i], a))).ToArray();
                if (missing.Length != 1) continue;
                int axis = missing[0];
                var values = supported.Where(p => Math.Abs(Coordinate(p.Normal, axis)) > 1e-6).Select(p =>
                    (p.Offset - Enumerable.Range(0, 3).Where(a => a != axis).Sum(a => Coordinate(vertices[i], a) * Coordinate(p.Normal, a)))
                    / Coordinate(p.Normal, axis)).Where(IsPlausibleCoordinate).Order().ToArray();
                var clusters = new List<double>();
                for (int j = 0; j + 2 < values.Length; j++)
                    if (values[j + 2] - values[j] < .001 && !clusters.Any(v => Math.Abs(v - values[j]) < .001))
                        clusters.Add((values[j] + values[j + 1] + values[j + 2]) / 3);
                // Three separately supported source planes must identify one
                // value using the two unchanged coordinates, even when this
                // vertex's serialized polygon references are unreadable.
                if (clusters.Count == 1) { vertices[i] = SetCoordinate(vertices[i], axis, clusters[0]); repaired++; }
            }
        }
        return repaired;
    }

    private static int RecoverRepeatedPairedCoordinates(List<Vertex> vertices, int[][][] polygons, CancellationToken token)
    {
        int repaired = 0;
        for (int axis = 0; axis < 3; axis++)
        foreach (var group in vertices.Select((v, i) => (Value: Coordinate(v, axis), Index: i)).GroupBy(v => v.Value)
            .Where(g => g.Key != 0 && g.Count() >= 3 && g.Count() <= vertices.Count * .10).ToArray())
        {
            var probe = vertices.ToList();
            foreach (var item in group) probe[item.Index] = SetCoordinate(probe[item.Index], axis, double.NaN);
            RecoverPairedCoordinatePlanes(probe, polygons, token);
            var anchors = group.Select(item => Coordinate(probe[item.Index], axis)).Where(IsPlausibleCoordinate).ToArray();
            double? recovered = anchors.Length >= 2 && anchors.Max() - anchors.Min() <= .01 ? anchors.Average() : null;
            recovered ??= RecoverPairedCapHeight(vertices, polygons, group.Select(v => v.Index).ToHashSet(), axis, token);
            if (recovered is null) continue;
            double value = recovered.Value;
            if (IsPlausibleCoordinate(group.Key) && Math.Abs(value - group.Key) < .01) continue;
            foreach (var item in group) vertices[item.Index] = SetCoordinate(vertices[item.Index], axis, value);
            repaired += group.Count();
        }
        return repaired;
    }

    private static double? RecoverPairedCapHeight(List<Vertex> vertices, int[][][] polygons, HashSet<int> cap, int axis, CancellationToken token)
    {
        var evidence = new List<(double Value, int Face, int A, int B)>();
        for (int f = 0; f < polygons.Length; f++)
        {
            token.ThrowIfCancellationRequested();
            var face = polygons[f];
            int[] unknown = face.SelectMany(ids => ids).Where(cap.Contains).Distinct().ToArray();
            int[] known = face.SelectMany(ids => ids).Where(i => !cap.Contains(i) && IsPlausible(vertices[i])).Distinct().ToArray();
            if (unknown.Length is < 2 or > 12 || known.Length is < 2 or > 24) continue;
            for (int a = 0; a < unknown.Length; a++)
            for (int b = a + 1; b < unknown.Length; b++)
            for (int c = 0; c < known.Length; c++)
            for (int d = c + 1; d < known.Length; d++)
            {
                Vertex u = SetCoordinate(vertices[unknown[a]], axis, 0), v = SetCoordinate(vertices[unknown[b]], axis, 0);
                Vertex p = vertices[known[c]], q = vertices[known[d]];
                Vertex n = Cross(Subtract(v, u), Subtract(q, p));
                double norm = Length(n);
                if (norm < 1e-8) continue;
                n = Multiply(n, 1 / norm);
                if (Math.Abs(Coordinate(n, axis)) < 1e-6) continue;
                double value = Dot(n, Subtract(p, u)) / Coordinate(n, axis), offset = Dot(n, p);
                if (!IsPlausibleCoordinate(value)) continue;
                // Two points on a shared cap and two intact points determine
                // its height by coplanarity. Every slot in this source polygon
                // must have a compatible vertex, not just the four anchors.
                if (!face.All(ids => ids.Any(i => (cap.Contains(i) || IsPlausible(vertices[i]))
                    && Math.Abs(Dot(n, cap.Contains(i) ? SetCoordinate(vertices[i], axis, value) : vertices[i]) - offset) < .01))) continue;
                evidence.Add((value, f, unknown[a], unknown[b]));
            }
        }
        var clusters = new List<double>();
        foreach (var item in evidence)
        {
            if (clusters.Any(value => Math.Abs(value - item.Value) < .01)) continue;
            var matching = evidence.Where(e => Math.Abs(e.Value - item.Value) < .001).ToArray();
            if (matching.Select(e => e.Face).Distinct().Count() < 3
                || matching.SelectMany(e => new[] { e.A, e.B }).Distinct().Count() < 3) continue;
            clusters.Add(matching.Average(e => e.Value));
        }
        return clusters.Count == 1 ? clusters[0] : null;
    }
}
