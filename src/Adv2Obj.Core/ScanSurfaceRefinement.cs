namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private sealed class ScanSurfaceGuide
    {
        private readonly (Vertex Normal, double Offset)[] planes;
        public double Scale { get; }

        public ScanSurfaceGuide(Mesh surface)
        {
            Vertex minimum = new(surface.Vertices.Min(v => v.X), surface.Vertices.Min(v => v.Y), surface.Vertices.Min(v => v.Z));
            Vertex maximum = new(surface.Vertices.Max(v => v.X), surface.Vertices.Max(v => v.Y), surface.Vertices.Max(v => v.Z));
            Scale = Distance(minimum, maximum);
            planes = surface.Faces.Select(f =>
            {
                Vertex a = surface.Vertices[f.A];
                Vertex n = Cross(Subtract(surface.Vertices[f.B], a), Subtract(surface.Vertices[f.C], a));
                n = Multiply(n, 1 / Length(n));
                return (Normal: n, Offset: Dot(n, a));
            }).GroupBy(p => (Math.Round(p.Normal.X, 8), Math.Round(p.Normal.Y, 8), Math.Round(p.Normal.Z, 8), Math.Round(p.Offset, 5)))
                .Select(g => g.First()).ToArray();
        }

        public double SignedDistance(Vertex v)
        {
            double result = double.NegativeInfinity;
            foreach (var plane in planes) result = Math.Max(result, Dot(plane.Normal, v) - plane.Offset);
            return result;
        }
    }

    private static Mesh? TryRefinePolygonSurface(byte[] payload, int denseHeader, Mesh polygonSurface, CancellationToken token)
    {
        try
        {
            int count = checked((int)((ReadUInt32(payload, denseHeader + 20) + 56L) / 56));
            if (count is < 1000 or > 25_000) return null;
            int faceCount = count * 2 - 4;
            var table = SegmentedTables(payload).FirstOrDefault(t => t.Count == faceCount
                && t.Start - 4 <= denseHeader + 28 + count * 24
                && t.Start - 4 >= denseHeader + 28 + count * 24 - 128);
            if (table.Count == 0) return null;
            var recordedFaces = new HashSet<(int, int, int)>();
            int end = Math.Min(payload.Length - 16, table.Start + faceCount * 16 + 512);
            for (int offset = table.Start; offset <= end; offset++)
            {
                if (ReadUInt32(payload, offset) != 3) continue;
                uint a = ReadUInt32(payload, offset + 4), b = ReadUInt32(payload, offset + 8), c = ReadUInt32(payload, offset + 12);
                if (a >= count || b >= count || c >= count || a == b || a == c || b == c) continue;
                recordedFaces.Add(FaceKey((int)a, (int)b, (int)c));
                offset += 15;
            }
            // Do not spend time rebuilding a scan whose source connectivity
            // cannot provide an independent check of the result.
            if (recordedFaces.Count < faceCount * .2) return null;
            var (decoded, repaired) = ReadAlignedVertices(payload, table.Start - 4 - count * 24,
                count, faceCount, token, polygonSurface);
            var guide = new ScanSurfaceGuide(polygonSurface);
            var measured = decoded.Select((point, index) => (Point: point, Source: index,
                Distance: guide.SignedDistance(point))).Where(v => v.Distance <= guide.Scale * .01
                    && v.Distance >= -guide.Scale * .03).ToList();
            if (measured.Count < count * .95) return null;
            Vertex center = new(polygonSurface.Vertices.Average(v => v.X),
                polygonSurface.Vertices.Average(v => v.Y), polygonSurface.Vertices.Average(v => v.Z));
            var directions = measured.Select(v => Subtract(v.Point, center)).ToList();
            if (directions.Any(v => Length(v) < guide.Scale * .05)) return null;
            directions = directions.Select(v => Multiply(v, 1 / Length(v))).ToList();
            Mesh sphere = BuildConvexHull(new(directions, [], 0, []), token);
            var directionToIndex = directions.Select((v, i) => (v, i)).GroupBy(v => v.v)
                .ToDictionary(g => g.Key, g => g.First().i);
            var kept = sphere.Vertices.Select(v => measured[directionToIndex[v]]).ToList();
            if (kept.Count < count * .95) return null;
            var mapping = kept.Select((v, i) => (v.Source, Index: i)).ToDictionary(v => v.Source, v => v.Index);
            var original = new HashSet<(int, int, int)>();
            foreach (var (a, b, c) in recordedFaces)
            {
                if (mapping.TryGetValue(a, out int x) && mapping.TryGetValue(b, out int y)
                    && mapping.TryGetValue(c, out int z)) original.Add(FaceKey(x, y, z));
            }
            if (original.Count < sphere.Faces.Count * .2) return null;
            RestoreSourceDiagonals(sphere.Faces, sphere.Vertices, original);
            int matched = sphere.Faces.Count(f => original.Contains(FaceKey(f.A, f.B, f.C)));
            System.Diagnostics.Trace.WriteLine($"Scan refinement: {kept.Count}/{count} points, {matched}/{original.Count} source faces agree.");
            if (matched < original.Count * .85) return null;
            Mesh refined = new(kept.Select(v => v.Point).ToList(), sphere.Faces, repaired, []);
            ValidateClosedTopology(refined.Vertices.Count, refined.Faces);
            double volumeRatio = Math.Abs(ScanVolume(refined) / ScanVolume(polygonSurface));
            if (!double.IsFinite(volumeRatio) || volumeRatio is < .95 or > 1.05) return null;
            refined.Warnings.Add($"Recovered {kept.Count:N0} of {count:N0} scan points using the stored polygon surface to validate byte alignment. "
                + $"Preserved {matched:N0} readable source triangles; other triangles are reconstructed. "
                + $"{repaired} coordinates repaired; {count - kept.Count} uncertain points omitted. Requires visual review.");
            return refined with { UsesRecoveredScanSurface = true };
        }
        catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); return null; }
    }

    private static double ScanVolume(Mesh mesh)
    {
        Vertex center = new(mesh.Vertices.Average(v => v.X), mesh.Vertices.Average(v => v.Y), mesh.Vertices.Average(v => v.Z));
        return mesh.Faces.Sum(f => Dot(Subtract(mesh.Vertices[f.A], center),
            Cross(Subtract(mesh.Vertices[f.B], center), Subtract(mesh.Vertices[f.C], center)))) / 6;
    }
}
