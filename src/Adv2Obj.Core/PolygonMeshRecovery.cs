namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static readonly byte[] PolygonMeshSignature =
        [0x95, 0xf2, 0xb2, 0x89, 0x27, 0x96, 0x3b, 0x48, 0xa7, 0xa2, 0x00, 0xcb, 0xa0, 0x2f, 0x27, 0xab];

    private sealed record PolygonRecord(int End, List<Vertex> Vertices, List<int[]> Polygons);

    private static Mesh? TryRecoverPolygonMesh(byte[] payload, CancellationToken token)
        => RecoverPolygonMesh(payload, token, allowScanRefinement: true);

    private static Mesh? RecoverPolygonMesh(byte[] payload, CancellationToken token, bool allowScanRefinement)
    {
        // Advisor stores two copies of a coarser polygon surface immediately
        // before the dense scan mesh. Only use that paired representation after
        // the dense mesh decoders fail; never substitute an unrelated plan mesh.
        int cursor = 0;
        while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, payload.Length)) >= 0)
        {
            token.ThrowIfCancellationRequested();
            int first = cursor++;
            try
            {
                PolygonRecord? record = ReadPolygonRecord(payload, first);
                if (record is null) continue;
                int second = record.End;
                if (second + 28 > payload.Length
                    || !payload.AsSpan(second, 16).SequenceEqual(PolygonMeshSignature)) continue;
                int count = record.Vertices.Count;
                int dense = second + (record.End - first);
                if (dense + 29 > payload.Length) continue;
                if (payload[dense] == 1) dense++;
                if (!payload.AsSpan(dense, 16).SequenceEqual(PolygonMeshSignature)) continue;
                uint denseLength = ReadUInt32(payload, dense + 20);
                // A closed triangular scan has F=2V-4 and a 56V-56 byte record.
                if ((denseLength + 56L) % 56 != 0 || (denseLength + 56L) / 56 <= count * 2) continue;
                // The copies must agree at the byte level, including unusual
                // encodings. Similar-looking coordinates are insufficient.
                if (!payload.AsSpan(first + 28, count * 24)
                    .SequenceEqual(payload.AsSpan(second + 28, count * 24))) continue;

                Vertex?[] decoded = record.Vertices.Select(v => (Vertex?)v).ToArray();
                var repaired = RepairPolygonCoordinates(decoded, record.Polygons);
                repaired.UnionWith(RepairPolygonCoordinateBytes(decoded, record.Polygons, token));
                int repairedCount = repaired.Count;
                if (decoded.Count(v => !IsPlausible(v!.Value)) > count * .02) continue;
                var valid = decoded.Select((v, i) => (Vertex: v!.Value, Index: i))
                    .Where(v => IsPlausible(v.Vertex)).ToList();
                if (valid.Count < count * .98) continue;
                List<Vertex> vertices = valid.Select(v => v.Vertex).ToList();
                var remap = valid.Select((v, i) => (v.Index, New: i)).ToDictionary(v => v.Index, v => v.New);
                var polygons = record.Polygons.Where(p => p.All(remap.ContainsKey))
                    .Select(p => p.Select(i => remap[i]).ToArray()).ToList();
                if (polygons.Count < record.Polygons.Count * .2) continue;

                Mesh hull = BuildConvexHull(new(vertices, [], repaired.Count, []), token);
                int supported = SupportedPolygons(hull, vertices, polygons);
                // A sign error in a repeated cap coordinate is detectable from
                // the other polygon planes. Accept a correction only if both
                // source polygon support and retained hull vertices improve.
                if (hull.Vertices.Count < vertices.Count * .98 || supported < polygons.Count * .9)
                {
                    var correction = RepairPolygonCap(vertices, polygons, hull, supported, token);
                    if (correction is null) continue;
                    (vertices, hull, supported, int changed) = correction.Value;
                    repairedCount += changed;
                }
                if (hull.Vertices.Count < count * .97 || supported < polygons.Count * .9) continue;
                ValidateClosedTopology(hull.Vertices.Count, hull.Faces);
                Mesh? refined = allowScanRefinement ? TryRefinePolygonSurface(payload, dense, hull, token) : null;
                if (refined is not null) return refined;
                hull.Warnings.Add($"Dense scan mesh could not be decoded; recovered the stored polygon surface "
                    + $"({hull.Vertices.Count:N0} vertices; {supported}/{polygons.Count} readable source polygons verified). "
                    + "This is a coarser surface, not an exact dense-scan export; review it in MeshLab.");
                if (repairedCount > 0 || hull.Vertices.Count < count)
                    hull.Warnings.Add($"Polygon recovery repaired {repairedCount} vertex coordinates; "
                        + $"{count - hull.Vertices.Count} unusable or interior vertices were omitted.");
                return new Mesh(hull.Vertices, hull.Faces, repairedCount, hull.Warnings) { UsesCoarseSurface = true };
            }
            catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); }
        }
        return null;
    }

    private static PolygonRecord? ReadPolygonRecord(byte[] payload, int start, int? boundaryEnd = null)
    {
        if (start + 28 > payload.Length) return null;
        uint length = boundaryEnd.HasValue ? checked((uint)(boundaryEnd.Value - start - 24)) : ReadUInt32(payload, start + 20);
        // For this three-valent closed polygon representation: 3V=2E,
        // F=V/2+2, serialized bytes=4+24V+4+4F+8E=38V+16.
        if (length < 16 || (length - 16) % 38 != 0) return null;
        int count = (int)((length - 16) / 38);
        if (count is < 8 or > 10_000 || (count & 1) != 0
            || (ReadUInt32(payload, start + 24) & 255) != (count & 255)) return null;
        int end = checked(start + 24 + (int)length);
        if (end > payload.Length) return null;
        var vertices = Enumerable.Range(0, count).Select(i => ReadVertex(payload, start + 28 + i * 24)).ToList();
        int cursor = start + 28 + count * 24;
        int faceCount = count / 2 + 2;
        if ((ReadUInt32(payload, cursor) & 255) != (faceCount & 255)) return null;
        cursor += 4;
        var polygons = new List<int[]>();
        for (int f = 0; f < faceCount; f++)
        {
            if (cursor + 4 > end) return null;
            int sides = (int)(ReadUInt32(payload, cursor) & 255);
            if (sides is < 3 or > 64 || cursor + 4L + sides * 4L > end) return null;
            int[] ids = Enumerable.Range(0, sides)
                .Select(i => (int)(ReadUInt32(payload, cursor + 4 + i * 4) & (boundaryEnd.HasValue && count <= 256 ? 255u : 65535u))).ToArray();
            polygons.Add(ids);
            cursor += 4 + sides * 4;
        }
        return cursor == end ? new(end, vertices, polygons) : null;
    }

    private static HashSet<int> RepairPolygonCoordinates(Vertex?[] vertices, List<int[]> polygons)
    {
        var repaired = new HashSet<int>();
        // Infer an invalid coordinate from its incident stored planes, using
        // only fully readable points to determine each plane.
        for (int i = 0; i < vertices.Length; i++)
        {
            Vertex v = vertices[i]!.Value;
            int[] missing = Enumerable.Range(0, 3).Where(a => !IsPlausibleCoordinate(Coordinate(v, a))).ToArray();
            if (missing.Length != 1) continue;
            int axis = missing[0];
            var candidates = new List<double>();
            foreach (int[] polygon in polygons.Where(p => p.Contains(i)))
            {
                var points = polygon.Where(j => j < vertices.Length && j != i)
                    .Select(j => vertices[j]!.Value).Where(IsPlausible).ToList();
                if (points.Count < 3) continue;
                Vertex a = points[0];
                Vertex b = points.MaxBy(p => Distance(a, p));
                Vertex c = points.MaxBy(p => Length(Cross(Subtract(b, a), Subtract(p, a))));
                Vertex normal = Cross(Subtract(b, a), Subtract(c, a));
                double size = Length(normal);
                if (size < 1e-8) continue;
                normal = Multiply(normal, 1 / size);
                double offset = Dot(normal, a), component = Coordinate(normal, axis);
                if (Math.Abs(component) < 1e-6 || points.Any(p => Math.Abs(Dot(normal, p) - offset) > .01)) continue;
                double known = Enumerable.Range(0, 3).Where(k => k != axis)
                    .Sum(k => Coordinate(normal, k) * Coordinate(v, k));
                double value = (offset - known) / component;
                if (IsPlausibleCoordinate(value)) candidates.Add(value);
            }
            if (candidates.Count < 2 || candidates.Max() - candidates.Min() > .01) continue;
            double replacement = candidates.Average();
            vertices[i] = SetCoordinate(v, axis, replacement);
            repaired.Add(i);
        }
        return repaired;
    }

    private static Vertex SetCoordinate(Vertex v, int axis, double value) => axis switch
    {
        0 => v with { X = value }, 1 => v with { Y = value }, _ => v with { Z = value },
    };

    private static HashSet<int> RepairPolygonCoordinateBytes(Vertex?[] vertices, List<int[]> polygons, CancellationToken token)
    {
        var invalid = Enumerable.Range(0, vertices.Length).SelectMany(i => Enumerable.Range(0, 3)
            .Where(a => !IsPlausibleCoordinate(Coordinate(vertices[i]!.Value, a))).Select(a => (Index: i, Axis: a))).ToList();
        if (invalid.Count == 0 || invalid.Count > vertices.Length * .05
            || invalid.Select(v => v.Axis).Distinct().Count() != 1) return [];
        int axis = invalid[0].Axis;
        var valid = vertices.Select(v => Coordinate(v!.Value, axis)).Where(IsPlausibleCoordinate).ToList();
        double low = valid.Min(), high = valid.Max(), margin = (high - low) * .1;
        var prefixes = valid.Select(v => (ulong)BitConverter.DoubleToInt64Bits(v) >> 56)
            .GroupBy(v => v).Where(g => g.Count() >= Math.Max(4, valid.Count * .05)).Select(g => g.Key);
        var knownPolygons = polygons.Where(p => p.All(i => i < vertices.Length)).ToList();
        if (knownPolygons.Count < polygons.Count * .2) return [];
        List<Vertex>? selected = null;
        foreach (ulong prefix in prefixes)
        {
            var candidate = vertices.Select(v => v!.Value).ToList();
            foreach (var item in invalid)
            {
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(Coordinate(candidate[item.Index], axis));
                double value = BitConverter.Int64BitsToDouble((long)((bits & 0x00ff_ffff_ffff_ffff) | (prefix << 56)));
                candidate[item.Index] = SetCoordinate(candidate[item.Index], axis, value);
            }
            if (candidate.Any(v => !IsPlausible(v) || Coordinate(v, axis) < low - margin
                || Coordinate(v, axis) > high + margin)) continue;
            Mesh hull = BuildConvexHull(new(candidate, [], 0, []), token);
            // A byte repair must retain every source point and agree with the
            // source polygon planes. Ambiguous byte candidates are rejected.
            if (hull.Vertices.Count != vertices.Length
                || SupportedPolygons(hull, candidate, knownPolygons) < knownPolygons.Count * .95) continue;
            if (selected is not null) return [];
            selected = candidate;
        }
        if (selected is null) return [];
        foreach (var item in invalid) vertices[item.Index] = selected[item.Index];
        return invalid.Select(v => v.Index).ToHashSet();
    }

    private static int SupportedPolygons(Mesh hull, List<Vertex> vertices, List<int[]> polygons)
    {
        var planes = hull.Faces.Select(f =>
        {
            Vertex a = hull.Vertices[f.A];
            Vertex normal = Cross(Subtract(hull.Vertices[f.B], a), Subtract(hull.Vertices[f.C], a));
            normal = Multiply(normal, 1 / Length(normal));
            return (Normal: normal, Offset: Dot(normal, a));
        }).ToArray();
        return polygons.Count(p => p.Distinct().Count() == p.Length
            && planes.Any(plane => p.All(i => Math.Abs(Dot(plane.Normal, vertices[i]) - plane.Offset) < .01)));
    }

    private static (List<Vertex> Vertices, Mesh Hull, int Supported, int Changed)? RepairPolygonCap(
        List<Vertex> vertices, List<int[]> polygons, Mesh original, int originalSupport, CancellationToken token, double maximumFraction = .05)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            var ordered = vertices.Select(v => Coordinate(v, axis)).Order().ToArray();
            double low = ordered[ordered.Length / 10], high = ordered[ordered.Length * 9 / 10];
            double span = high - low;
            if (span < 1e-6) continue;
            foreach (var group in vertices.Select((v, i) => (Value: Coordinate(v, axis), Index: i))
                .GroupBy(v => v.Value).Where(g => g.Count() >= 3 && g.Count() <= vertices.Count * maximumFraction))
            {
                double value = group.Key;
                if (value >= low - span * .25 && value <= high + span * .25) continue;
                if (-value < low - span * .25 || -value > high + span * .25) continue;
                var changed = vertices.ToList();
                foreach (var v in group) changed[v.Index] = SetCoordinate(changed[v.Index], axis, -value);
                Mesh hull = BuildConvexHull(new(changed, [], 0, []), token);
                if (hull.Vertices.Count <= original.Vertices.Count || hull.Vertices.Count < vertices.Count * .98) continue;
                int support = SupportedPolygons(hull, changed, polygons);
                if (support <= originalSupport || support < polygons.Count * .95) continue;
                return (changed, hull, support, group.Count());
            }
        }
        return null;
    }
}
