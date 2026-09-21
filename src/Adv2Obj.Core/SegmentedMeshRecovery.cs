namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    // Used only after the ordinary decoders fail. A counted triangle table and
    // substantial surviving connectivity are required; byte plausibility alone
    // is not evidence of a recoverable surface.
    private static IEnumerable<(int Start, int Count)> SegmentedTables(byte[] payload)
    {
        for (int offset = 8; offset <= payload.Length - 16; offset++)
        {
            if (ReadUInt32(payload, offset) != 3) continue;
            int end = offset + 16;
            while (end <= payload.Length - 16 && ReadUInt32(payload, end) == 3) end += 16;
            int run = (end - offset) / 16;
            uint count = ReadUInt32(payload, offset - 4);
            if (run >= 1000 && count >= run && count <= 50_000 && (count & 1) == 0
                && offset - 4 - ((count + 4) / 2) * 24 > 8)
                yield return (offset, (int)count);
            offset = end - 1;
        }
    }

    private static (byte[] Payload, int RecordEnd, bool AlternateRawMesh)? FindSegmentedRawPayload(
        byte[] source, int metadataOffset)
    {
        // The first table is the rough mesh. Do not substitute a later plan's
        // geometry just because it is easier to decode.
        foreach (var (start, count) in SegmentedTables(source))
        {
            if (start < metadataOffset || start - metadataOffset > 2_000_000) continue;
            int end = Math.Min(source.Length, start + count * 16 + 512);
            int vertexCount = (count + 4) / 2;
            int recordEnd = -1;
            for (int offset = start; offset <= end - 16; offset++)
            {
                int cursor = offset;
                while (cursor <= end - 16 && ReadUInt32(source, cursor) == 3
                    && ReadUInt32(source, cursor + 4) < vertexCount
                    && ReadUInt32(source, cursor + 8) < vertexCount
                    && ReadUInt32(source, cursor + 12) < vertexCount) cursor += 16;
                if (cursor - offset >= 32 * 16) recordEnd = cursor;
                if (cursor > offset) offset = cursor - 1;
            }
            if (recordEnd < 0) return null;
            return (source.AsSpan(metadataOffset, recordEnd - metadataOffset).ToArray(), recordEnd, false);
        }
        return null;
    }

    private static Mesh? TryRecoverSegmentedMesh(byte[] payload, CancellationToken cancellationToken)
    {
        foreach (var (start, count) in SegmentedTables(payload))
        {
            cancellationToken.ThrowIfCancellationRequested();
            int vertexCount = (count + 4) / 2;
            int vertexStart = start - 4 - vertexCount * 24;
            int end = Math.Min(payload.Length - 16, start + count * 16 + 512);
            var faces = new List<(int A, int B, int C)>();
            var keys = new HashSet<(int, int, int)>();
            for (int offset = start; offset <= end; offset++)
            {
                if (ReadUInt32(payload, offset) != 3) continue;
                uint a = ReadUInt32(payload, offset + 4), b = ReadUInt32(payload, offset + 8),
                    c = ReadUInt32(payload, offset + 12);
                if (a >= vertexCount || b >= vertexCount || c >= vertexCount
                    || a == b || b == c || c == a) continue;
                if (keys.Add(FaceKey((int)a, (int)b, (int)c))) faces.Add(((int)a, (int)b, (int)c));
                offset += 15;
            }
            if (faces.Count < count * .99 || faces.Count > count) continue;
            try
            {
                // Non-manifold index corruption cannot be solved by moving
                // vertices. Reject it before attempting coordinate recovery.
                var boundary = SparseMeshBoundary(faces);
                if (boundary is null) continue;
                var (vertices, repaired) = ReadAlignedVertices(payload, vertexStart, vertexCount, count, cancellationToken);
                int sourceCount = faces.Count;
                foreach (var loop in boundary)
                {
                    var cap = TriangulatePlanarBoundary(loop, vertices, out bool complete);
                    if (!complete) throw new AdvFormatException("A missing mesh patch could not be triangulated.");
                    faces.AddRange(cap);
                }
                ValidateClosedTopology(vertexCount, faces);
                Mesh mesh = new(vertices, faces, repaired,
                    [$"Recovered byte-shifted mesh records; retained all {vertexCount:N0} vertices and {sourceCount:N0} source triangles, reconstructed {faces.Count - sourceCount} triangles and repaired {repaired} vertices. Requires visual review."]);
                return mesh;
            }
            catch (AdvFormatException error) { System.Diagnostics.Trace.WriteLine(error.Message); }
        }
        return null;
    }

    private static List<List<int>>? SparseMeshBoundary(List<(int A, int B, int C)> faces)
    {
        var edges = new Dictionary<(int, int), List<(int A, int B)>>();
        foreach (var (a, b, c) in faces)
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(x, y), Math.Max(x, y));
                if (!edges.TryGetValue(key, out var incident)) edges[key] = incident = [];
                incident.Add((x, y));
                if (incident.Count > 2) return null;
            }
        var next = new Dictionary<int, int>();
        var incoming = new HashSet<int>();
        foreach (var edge in edges.Values)
        {
            if (edge.Count == 2)
            {
                if (edge[0].A != edge[1].B || edge[0].B != edge[1].A) return null;
                continue;
            }
            if (!next.TryAdd(edge[0].A, edge[0].B) || !incoming.Add(edge[0].B)) return null;
        }
        if (next.Count > faces.Count * .01) return null;
        var loops = new List<List<int>>();
        while (next.Count > 0)
        {
            int first = next.Keys.First(), current = first;
            var loop = new List<int>();
            do
            {
                if (!next.Remove(current, out int following) || loop.Count >= 16) return null;
                loop.Add(current);
                current = following;
            } while (current != first);
            if (loop.Count < 3) return null;
            loops.Add(loop);
        }
        return loops;
    }

    private static (List<Vertex> Vertices, int Repaired) ReadAlignedVertices(
        byte[] payload, int start, int count, int faceCount, CancellationToken cancellationToken,
        Mesh? surfaceGuide = null)
    {
        int missing = -1;
        long declaredLength = 8L + count * 24L + faceCount * 16L;
        for (int shift = 0; shift <= 128; shift++)
        {
            if (ReadUInt32(payload, start + shift - 8) != declaredLength) continue;
            if (missing >= 0) throw new AdvFormatException("Ambiguous shifted vertex boundary.");
            missing = shift;
        }
        if (missing < 0) throw new AdvFormatException("No declared boundary for shifted coordinates.");
        int states = missing + 1;
        var history = new byte[count, states];
        var previous = Enumerable.Repeat(double.NegativeInfinity, states).ToArray();
        previous[missing] = 0;
        var previousVertices = new Vertex[states];
        var guide = surfaceGuide is null ? null : new ScanSurfaceGuide(surfaceGuide);
        var guideCosts = new Dictionary<int, double>();
        double[] scales = new double[3];
        for (int axis = 0; axis < 3; axis++)
        {
            var steps = new List<double>();
            foreach (int begin in new[] { start + missing, start + (count - 128) * 24 })
                for (int i = 1; i < 128; i++)
                {
                    double a = Coordinate(ReadVertex(payload, begin + (i - 1) * 24), axis);
                    double b = Coordinate(ReadVertex(payload, begin + i * 24), axis);
                    if (IsSymbolCoordinate(a) && IsSymbolCoordinate(b)) steps.Add(Math.Abs(a - b));
                }
            if (steps.Count < 64) throw new AdvFormatException("Insufficient intact coordinate runs to establish alignment.");
            steps.Sort();
            scales[axis] = Math.Max(1, Median(steps));
        }
        // Switching alignment has a cost, so isolated plausible doubles cannot
        // change the record boundary. A sustained run must support each shift.
        for (int i = 0; i < count; i++)
        {
            if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            var next = new double[states];
            var nextVertices = new Vertex[states];
            for (int state = 0; state < states; state++)
            {
                int address = start + i * 24 + state;
                Vertex v = ReadVertex(payload, address);
                nextVertices[state] = v;
                int good = (IsSymbolCoordinate(v.X) ? 1 : 0)
                    + (IsSymbolCoordinate(v.Y) ? 1 : 0) + (IsSymbolCoordinate(v.Z) ? 1 : 0);
                double best = double.NegativeInfinity;
                int parent = state;
                for (int candidate = state; candidate < states; candidate++)
                {
                    if (i == 0 && state != missing) continue;
                    double continuity = 0;
                    Vertex before = previousVertices[candidate];
                    if (i > 0 && good == 3 && IsSymbolCoordinate(before.X)
                        && IsSymbolCoordinate(before.Y) && IsSymbolCoordinate(before.Z))
                        for (int axis = 0; axis < 3; axis++)
                            continuity += Math.Min(30, Math.Abs(Coordinate(v, axis) - Coordinate(before, axis)) / scales[axis]);
                    double score = previous[candidate] - (candidate == state ? 0 : 30 + (candidate - state) * .1) - continuity;
                    if (score > best) { best = score; parent = candidate; }
                }
                history[i, state] = (byte)parent;
                double guideCost = 0;
                if (guide is not null && !guideCosts.TryGetValue(address, out guideCost))
                {
                    double distance = good == 3 ? guide.SignedDistance(v) : double.PositiveInfinity;
                    guideCost = Math.Min(120, Math.Abs(distance) / (guide.Scale * .002) * (distance > 0 ? 15 : 10));
                    guideCosts[address] = guideCost;
                }
                next[state] = best + good * 4 + (good == 3 ? 6 : 0) - guideCost;
            }
            previous = next;
            previousVertices = nextVertices;
        }
        int last = 0; // The final vertex ends at the counted face table.
        Vertex?[] vertices = new Vertex?[count];
        int valid = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            Vertex v = ReadVertex(payload, start + i * 24 + last);
            bool x = IsSymbolCoordinate(v.X), y = IsSymbolCoordinate(v.Y), z = IsSymbolCoordinate(v.Z);
            if (x && y && z) valid++;
            vertices[i] = new Vertex(x ? v.X : double.NaN, y ? v.Y : double.NaN, z ? v.Z : double.NaN);
            last = history[i, last];
        }
        if (valid < count * .95)
            throw new AdvFormatException("Too few coordinates validate after record realignment.");
        var repaired = RepairInvalidCoordinates(vertices);
        return (vertices.Select(v => v!.Value).ToList(), repaired.Count);
    }
}
