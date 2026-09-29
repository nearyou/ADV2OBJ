namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    // A typed record supplies both counts. Unlike the old face-run search, this
    // also handles closed scans whose topology does not satisfy F = 2V - 4.
    private static IEnumerable<(int Header, int Vertices, int Faces, int Table, int End)> DeclaredScans(byte[] data)
    {
        int cursor = 0;
        while ((cursor = FindBytes(data, PolygonMeshSignature, cursor, data.Length)) >= 0)
        {
            int header = cursor++;
            if (header + 28 > data.Length) continue;
            uint length = ReadUInt32(data, header + 20), count = ReadUInt32(data, header + 24);
            if (count > 100_000 && (length + 56L) % 56 == 0
                && ((length + 56L) / 56) == (count & 65535))
                count &= 65535; // Corroborated below by the independent face count.
            if (count is < 1000 or > 100_000 || length < 8 + count * 24L) continue;
            long faceBytes = length - 8 - count * 24L;
            if (faceBytes % 16 != 0 || faceBytes / 16 < count || faceBytes / 16 > count * 3L) continue;
            int faces = (int)(faceBytes / 16);
            long expectedTable = header + 28L + count * 24L;
            if (expectedTable + 4 > data.Length) continue;
            for (int shift = 0; shift <= 128; shift++)
            {
                int table = (int)expectedTable - shift;
                if (ReadUInt32(data, table) != faces || table + 20 > data.Length || ReadUInt32(data, table + 4) != 3) continue;
                yield return (header, (int)count, faces, table,
                    (int)Math.Min(data.Length, header + 24L + length));
                break;
            }
        }
    }

    private static Mesh? TryRecoverDeclaredMesh(byte[] payload, CancellationToken token, bool allowShifted = false)
    {
        foreach (var record in DeclaredScans(payload))
        {
            token.ThrowIfCancellationRequested();
            List<Vertex> vertices;
            int repaired = 0;
            if (record.Table == record.Header + 28 + record.Vertices * 24)
            {
                vertices = Enumerable.Range(0, record.Vertices)
                    .Select(i => ReadVertex(payload, record.Header + 28 + i * 24)).ToList();
                if (vertices.Any(v => !IsPlausible(v))) continue;
            }
            else
            {
                if (!allowShifted) continue;
                Mesh? guide = FindIntactPolygonGuide(payload, record.Header, token);
                if (guide is null) continue;
                try { (vertices, repaired) = ReadAlignedVertices(payload, record.Table - record.Vertices * 24,
                    record.Vertices, record.Faces, token, guide); }
                catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); continue; }
            }
            var faces = new List<(int A, int B, int C)>();
            var keys = new HashSet<(int, int, int)>();
            bool duplicate = false;
            for (int offset = record.Table + 4; offset <= record.End - 16; offset++)
            {
                if (ReadUInt32(payload, offset) != 3) continue;
                uint a = ReadUInt32(payload, offset + 4), b = ReadUInt32(payload, offset + 8), c = ReadUInt32(payload, offset + 12);
                if (a >= record.Vertices || b >= record.Vertices || c >= record.Vertices || a == b || a == c || b == c) continue;
                if (keys.Add(FaceKey((int)a, (int)b, (int)c))) faces.Add(((int)a, (int)b, (int)c));
                else duplicate = true;
                offset += 15;
            }
            if (duplicate || faces.Count < record.Faces * .99 || faces.Count > record.Faces) continue;
            var sourceFaces = faces.ToList();
            var sourceVertices = vertices.ToList();
            for (int attempt = 0; attempt < 2; attempt++)
            try
            {
                faces = sourceFaces.ToList(); vertices = sourceVertices.ToList();
                int ears = attempt == 0 ? 0 : RemoveBoundaryEars(faces);
                if (attempt > 0 && ears == 0) continue;
                // Gaps can meet at a vertex. Open a small local patch around
                // those junctions before triangulating its simple boundary.
                int removed = OpenBoundaryJunctions(faces);
                if (removed < 0 || removed > record.Faces * .005) continue;
                var loops = SparseMeshBoundary(faces);
                if (loops is null) continue;
                int retained = faces.Count;
                var usedEdges = faces.SelectMany(f => new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) })
                    .Select(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))).ToHashSet();
                foreach (var loop in loops)
                {
                    var cap = TriangulatePlanarBoundary(loop, vertices, out bool complete, usedEdges);
                    if (!complete)
                    {
                        cap = TriangulateSpatialGap(loop, vertices, usedEdges, out complete);
                    }
                    if (!complete) throw new AdvFormatException("A source triangle gap could not be triangulated.");
                    faces.AddRange(cap);
                }
                if (!RestorePatchVertices(vertices, faces, retained)) { System.Diagnostics.Trace.WriteLine("Patch interior vertices could not be restored."); continue; }
                if (CountInvalidEdges(faces) != 0) { System.Diagnostics.Trace.WriteLine("Patch edges do not close."); continue; }
                if (faces.Count != record.Faces) { System.Diagnostics.Trace.WriteLine($"Patch face count {faces.Count}, expected {record.Faces}; vertices {vertices.Count}."); continue; }
                if (record.Table != record.Header + 28 + record.Vertices * 24)
                {
                    var lengths = sourceFaces.Select(f => Math.Max(Distance(vertices[f.A], vertices[f.B]),
                        Math.Max(Distance(vertices[f.B], vertices[f.C]), Distance(vertices[f.C], vertices[f.A])))).Order().ToArray();
                    double scale = Distance(new(vertices.Min(v => v.X), vertices.Min(v => v.Y), vertices.Min(v => v.Z)),
                        new(vertices.Max(v => v.X), vertices.Max(v => v.Y), vertices.Max(v => v.Z)));
                    // Plausible doubles alone cannot establish vertex identity:
                    // a whole-vertex shift can fold an otherwise closed scan.
                    // Reject a sustained population of edges spanning the stone.
                    double limit = Math.Max(lengths[lengths.Length / 2] * 10, scale * .1);
                    if (lengths.Count(v => v > limit) > lengths.Length * .01)
                    {
                        Mesh? polygon = TryRecoverStoredPolygon(payload, token);
                        polygon?.Warnings.Add("The shifted scan coordinates conflict with source triangle adjacency; retained the verified stored polygon surface.");
                        return polygon;
                    }
                }
                return new(vertices, faces, repaired,
                    [$"Recovered the counted scan: retained {vertices.Count:N0} vertices and {retained:N0} source triangles; "
                    + $"repaired {repaired} coordinates and reconstructed {faces.Count - retained} triangles around incomplete records. Requires visual review."]);
            }
            catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); }
        }
        return null;
    }

    private static int RemoveBoundaryEars(List<(int A, int B, int C)> faces)
    {
        var edges = faces.SelectMany(f => new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) })
            .GroupBy(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2)))
            .ToDictionary(g => g.Key, g => g.Count());
        // A missing size word may turn an index value of 3 into a false face.
        // Retract dangling boundary ears only after ordinary closure fails.
        return faces.RemoveAll(f => new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) }
            .Count(e => edges[(Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))] == 1) >= 2);
    }

    private static Mesh? FindIntactPolygonGuide(byte[] payload, int denseHeader, CancellationToken token)
    {
        int cursor = 0;
        Mesh? guide = null;
        while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, denseHeader)) >= 0)
        {
            int header = cursor++;
            var polygon = ReadPolygonRecord(payload, header);
            if (polygon is null || polygon.Vertices.Any(v => !IsPlausible(v))
                || polygon.Polygons.Any(p => p.Any(i => i >= polygon.Vertices.Count))) continue;
            try
            {
                Mesh hull = BuildConvexHull(new(polygon.Vertices, [], 0, []), token);
                if (hull.Vertices.Count < polygon.Vertices.Count * .98
                    || SupportedPolygons(hull, polygon.Vertices, polygon.Polygons) < polygon.Polygons.Count * .95) continue;
                guide = hull;
            }
            catch (AdvFormatException) { }
        }
        return guide;
    }

    private static bool RestorePatchVertices(List<Vertex> vertices, List<(int A, int B, int C)> faces, int retained)
    {
        var used = faces.SelectMany(f => new[] { f.A, f.B, f.C }).ToHashSet();
        foreach (int missing in Enumerable.Range(0, vertices.Count).Where(i => !used.Contains(i)))
        {
            int selected = -1;
            double best = double.PositiveInfinity;
            for (int i = retained; i < faces.Count; i++)
            {
                var (a, b, c) = faces[i];
                Vertex u = Subtract(vertices[b], vertices[a]), v = Subtract(vertices[c], vertices[a]);
                Vertex p = Subtract(vertices[missing], vertices[a]);
                double uu = Dot(u, u), uv = Dot(u, v), vv = Dot(v, v), pu = Dot(p, u), pv = Dot(p, v);
                double denominator = uu * vv - uv * uv;
                if (denominator <= 1e-12) continue;
                double s = (pu * vv - pv * uv) / denominator, t = (pv * uu - pu * uv) / denominator;
                // A patch vertex must lie inside a newly created triangle. Do
                // not move it onto a boundary or another region of the scan.
                if (s <= 1e-9 || t <= 1e-9 || s + t >= 1 - 1e-9) continue;
                double distance = Length(Subtract(p, Add(Multiply(u, s), Multiply(v, t))));
                if (distance > Math.Sqrt(Math.Max(uu, vv)) * .2 || distance >= best) continue;
                best = distance;
                selected = i;
            }
            if (selected < 0) return false;
            var face = faces[selected];
            faces[selected] = (face.A, face.B, missing);
            faces.Add((face.B, face.C, missing));
            faces.Add((face.C, face.A, missing));
        }
        return true;
    }

    private static (byte[] Payload, int RecordEnd, bool AlternateRawMesh)? FindDeclaredRawPayload(byte[] source, int metadata)
    {
        foreach (var record in DeclaredScans(source))
        {
            if (record.Header < metadata) continue;
            if (record.Header - metadata > 16_000_000) return null;
            return (source.AsSpan(metadata, record.End - metadata).ToArray(), record.End, false);
        }
        return null;
    }

    private static int OpenBoundaryJunctions(List<(int A, int B, int C)> faces)
    {
        int removed = 0;
        for (int pass = 0; pass < 3; pass++)
        {
            var edges = new Dictionary<(int, int), List<(int A, int B)>>();
            foreach (var (a, b, c) in faces)
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = (Math.Min(x, y), Math.Max(x, y));
                    if (!edges.TryGetValue(key, out var incident)) edges[key] = incident = [];
                    incident.Add((x, y));
                }
            if (edges.Values.Any(e => e.Count > 2 || e.Count == 2 && e[0] != (e[1].B, e[1].A))) return -1;
            var junctions = edges.Values.Where(e => e.Count == 1).Select(e => e[0].A)
                .GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            if (junctions.Count == 0) return removed;
            removed += faces.RemoveAll(f => junctions.Contains(f.A) || junctions.Contains(f.B) || junctions.Contains(f.C));
            if (removed > 64) return -1;
        }
        return -1;
    }
}
