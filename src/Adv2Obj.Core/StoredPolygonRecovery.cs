namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static Mesh? RecoverStoredRawSurface(byte[] source, ref int recordEnd, CancellationToken token)
    {
        int start = checked((int)ReadUInt32(source, 0x34));
        if (FindBytes(source, ZippedDataName, start, Math.Min(source.Length, start + 1024)) >= 0) return null;
        byte[] region = source.AsSpan(start, Math.Min(source.Length - start, 16_000_000)).ToArray();
        foreach (int header in StoredSurfaceHeaders(region).Order())
        {
            long end = header + 24L + ReadUInt32(region, header + 20);
            if (end > region.Length || end < header + 28) continue;
            Mesh? surface = TryRecoverStoredPolygon(region.AsSpan(0, (int)end).ToArray(), token);
            if (surface is null) continue;
            recordEnd = start + (int)end;
            return surface;
        }
        return null;
    }

    private static Mesh? TryRecoverStoredPolygon(byte[] payload, CancellationToken token)
    {
        foreach (int denseHeader in StoredSurfaceHeaders(payload))
        {
            token.ThrowIfCancellationRequested();
            var headers = new List<int>();
            int cursor = 0;
            while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, denseHeader)) >= 0) headers.Add(cursor++);
            if (headers.Count < 2 || payload[denseHeader - 1] != 1) continue;
            for (int copy = 0; copy < 2; copy++)
            {
                int first = headers[headers.Count - 2 + copy], end = copy == 0 ? headers[^1] : denseHeader - 1;
                var record = ReadPolygonRecord(payload, first, end);
                if (record is null) { System.Diagnostics.Trace.WriteLine($"Polygon boundary {first}..{end} could not be parsed."); continue; }
                try
                {
                    var points = record.Vertices.Select(v => (Vertex?)v).ToArray();
                    var repaired = RepairPolygonCoordinates(points, record.Polygons);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var damagedGroups = record.Vertices.Select((v, i) => (Value: Coordinate(v, axis), Index: i))
                            .Where(v => !IsPlausibleCoordinate(v.Value)).GroupBy(v => BitConverter.DoubleToInt64Bits(v.Value));
                        foreach (var group in damagedGroups)
                        {
                            var anchors = group.Where(v => repaired.Contains(v.Index))
                                .Select(v => Coordinate(points[v.Index]!.Value, axis)).Where(IsPlausibleCoordinate).ToList();
                            if (anchors.Count < 2 || anchors.Max() - anchors.Min() > .001) continue;
                            // Two independently solved incident-plane intersections
                            // corroborate the repeated coordinate; final surface
                            // and source-plane checks still validate every point.
                            foreach (var item in group.Where(v => !IsPlausibleCoordinate(Coordinate(points[v.Index]!.Value, axis))))
                            {
                                points[item.Index] = SetCoordinate(points[item.Index]!.Value, axis, anchors.Average());
                                repaired.Add(item.Index);
                            }
                        }
                    }
                    repaired.UnionWith(RepairPolygonCoordinateBytes(points, record.Polygons, token));
                    if (points.Any(v => !IsPlausible(v!.Value))) { System.Diagnostics.Trace.WriteLine($"Polygon has {points.Count(v => !IsPlausible(v!.Value))} invalid points after {repaired.Count} repairs."); continue; }
                    var vertices = points.Select(v => v!.Value).ToList();
                    var polygons = record.Polygons.Where(p => p.All(i => i < vertices.Count)).ToList();
                    if (polygons.Count < record.Polygons.Count * .5) continue;
                    Mesh hull = BuildConvexHull(new(vertices, [], repaired.Count, []), token);
                    int capRepairs = 0;
                    int originalSupport = SupportedPolygons(hull, vertices, polygons);
                    if (hull.Vertices.Count < vertices.Count * .97 || originalSupport < polygons.Count * .95)
                    {
                        var cap = RepairPolygonCap(vertices, polygons, hull, originalSupport, token, maximumFraction: .25);
                        if (cap is not null) (vertices, hull, _, capRepairs) = cap.Value;
                    }
                    int repairedIndices = RepairPolygonReferences(hull, vertices, polygons);
                    int supported = SupportedPolygons(hull, vertices, polygons);
                    System.Diagnostics.Trace.WriteLine($"Stored polygon: {hull.Vertices.Count}/{vertices.Count} points; {supported}/{polygons.Count} supported polygons; {repaired.Count} repairs.");
                    if (hull.Vertices.Count < vertices.Count * .97 || supported < polygons.Count * .95) continue;
                    ValidateClosedTopology(hull.Vertices.Count, hull.Faces);
                    return new Mesh(hull.Vertices, hull.Faces, repaired.Count + capRepairs,
                        [$"Recovered the independently counted polygon record preceding the scan ({hull.Vertices.Count} vertices; {supported}/{polygons.Count} source planes verified; {repaired.Count + capRepairs} coordinates and {repairedIndices} polygon references repaired). Coarser than the dense scan; requires visual review."])
                        { UsesCoarseSurface = true };
                }
                catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); }
            }
        }
        return null;
    }

    private static IEnumerable<int> StoredSurfaceHeaders(byte[] payload)
    {
        var seen = new HashSet<int>();
        foreach (var dense in DeclaredScans(payload))
            if (seen.Add(dense.Header)) yield return dense.Header;
        int cursor = 0;
        while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, payload.Length)) >= 0)
        {
            int header = cursor++;
            if (header + 28 > payload.Length || seen.Contains(header)) continue;
            long length = ReadUInt32(payload, header + 20), count = ReadUInt32(payload, header + 24);
            long faceBytes = length - 8 - count * 24;
            bool counted = count is >= 1000 and <= 250_000 && faceBytes >= count * 16
                && faceBytes <= count * 48 && faceBytes % 16 == 0;
            if (counted || length == 0 && CountFaceRecords(payload) >= 1000) yield return header;
        }
    }

    private static int RepairPolygonReferences(Mesh hull, List<Vertex> vertices, List<int[]> polygons)
    {
        var unsupported = polygons.Where(p => SupportedPolygons(hull, vertices, [p]) == 0).ToList();
        int repaired = 0;
        foreach (int index in unsupported.SelectMany(p => p).Distinct().ToArray())
        {
            var incident = unsupported.Where(p => p.Contains(index)).ToList();
            if (incident.Count < 3) continue;
            // Three independent polygon planes must agree on exactly one of
            // the stored vertices. Never move a vertex to fit damaged indices.
            var candidates = Enumerable.Range(0, vertices.Count).Where(i => i != index && incident.All(p =>
                SupportedPolygons(hull, vertices, [p.Select(v => v == index ? i : v).ToArray()]) == 1)).ToList();
            if (candidates.Count != 1) continue;
            foreach (var polygon in incident)
                for (int j = 0; j < polygon.Length; j++)
                    if (polygon[j] == index) { polygon[j] = candidates[0]; repaired++; }
        }
        return repaired;
    }
}
