namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static Mesh? TryRecoverPointSurface(byte[] payload, CancellationToken cancellationToken)
    {
        try { return RecoverPointSurface(payload, cancellationToken); }
        catch (AdvFormatException) { return null; }
    }

    private static Mesh? RecoverPointSurface(byte[] payload, CancellationToken cancellationToken)
    {
        var table = FindPartialFaceTable(payload);
        if (table is null) return null;
        var (faceStart, readableFaces, vertexCount) = table.Value;
        int start = faceStart - vertexCount * 24;
        if (start < 8 || vertexCount > 25_000) return null;
        uint faceCount = ReadUInt32(payload, faceStart);
        if (ReadUInt32(payload, start - 8) != 8L + vertexCount * 24L + faceCount * 16L
            || ReadUInt32(payload, start - 4) != vertexCount) return null;
        var vertices = Enumerable.Range(0, vertexCount).Select(i => ReadVertex(payload, start + i * 24)).ToList();
        if (vertices.Any(vertex => !IsPlausible(vertex))) return null;
        Vertex center = new(vertices.Average(p => p.X), vertices.Average(p => p.Y), vertices.Average(p => p.Z));
        var directions = vertices.Select(p => Subtract(p, center)).ToList();
        if (directions.Any(p => Length(p) < 1e-8)) return null;
        directions = directions.Select(p => Multiply(p, 1 / Length(p))).ToList();
        Mesh hull = BuildConvexHull(new(directions, [], 0, []), cancellationToken);
        if (hull.Vertices.Count != vertexCount) return null;
        var sourceFaces = new HashSet<(int, int, int)>();
        for (int i = 0; i < readableFaces; i++)
        {
            int offset = faceStart + 4 + i * 16;
            if (!FaceMarkers.Contains(ReadUInt32(payload, offset))
                || !DecodeUnique(ReadUInt32(payload, offset + 4), (uint)vertexCount, out uint a)
                || !DecodeUnique(ReadUInt32(payload, offset + 8), (uint)vertexCount, out uint b)
                || !DecodeUnique(ReadUInt32(payload, offset + 12), (uint)vertexCount, out uint c)
                || a == b || b == c || c == a) continue;
            sourceFaces.Add(FaceKey((int)a, (int)b, (int)c));
        }
        RestoreSourceDiagonals(hull.Faces, directions, sourceFaces);
        int confirmed = hull.Faces.Count(face => sourceFaces.Contains(FaceKey(face.A, face.B, face.C)));
        // Point-based recovery is only appropriate for a single radial surface.
        // Require substantial agreement with the surviving source connectivity;
        // disconnected, folded and multi-shell data must use a different decoder.
        if (confirmed < hull.Faces.Count * .8) return null;
        // Hull vertex order is the sorted input index order when no points are lost.
        var result = new Mesh(vertices, hull.Faces, 0,
            [$"The triangle table is incomplete; preserved all {vertexCount:N0} measured points and recovered {confirmed:N0} source triangles. The remaining {hull.Faces.Count - confirmed:N0} triangles are reconstructed and require review."]);
        ValidateClosedTopology(vertexCount, result.Faces);
        return result;
    }

    private static (int, int, int) FaceKey(int a, int b, int c)
    {
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return (a, b, c);
    }

    private static void RestoreSourceDiagonals(List<(int A, int B, int C)> faces,
        List<Vertex> directions, HashSet<(int, int, int)> original)
    {
        for (int pass = 0; pass < 32; pass++)
        {
            var edges = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < faces.Count; i++)
            {
                var (a, b, c) = faces[i];
                foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
                {
                    var key = (Math.Min(x, y), Math.Max(x, y));
                    if (!edges.TryGetValue(key, out var incident)) edges[key] = incident = [];
                    incident.Add(i);
                }
            }
            HashSet<int> changed = [];
            foreach (var entry in edges)
            {
                var incident = entry.Value;
                if (incident.Count != 2 || incident.Any(changed.Contains)) continue;
                int first = incident[0], second = incident[1];
                var face = faces[first];
                int a = face.A, b = face.B, c = face.C;
                while (Math.Min(a, b) != entry.Key.Item1 || Math.Max(a, b) != entry.Key.Item2)
                    (a, b, c) = (b, c, a);
                var other = faces[second];
                int d = new[] { other.A, other.B, other.C }.First(v => v != a && v != b);
                if (edges.ContainsKey((Math.Min(c, d), Math.Max(c, d)))) continue;
                int before = (original.Contains(FaceKey(a, b, c)) ? 1 : 0) + (original.Contains(FaceKey(b, a, d)) ? 1 : 0);
                int after = (original.Contains(FaceKey(c, d, b)) ? 1 : 0) + (original.Contains(FaceKey(d, c, a)) ? 1 : 0);
                if (after <= before) continue;
                bool Outward(int x, int y, int z) => Dot(Cross(Subtract(directions[y], directions[x]),
                    Subtract(directions[z], directions[x])), directions[x]) > 1e-12;
                if (!Outward(c, d, b) || !Outward(d, c, a)) continue;
                faces[first] = (c, d, b);
                faces[second] = (d, c, a);
                changed.Add(first); changed.Add(second);
            }
            if (changed.Count == 0) break;
        }
    }
}
