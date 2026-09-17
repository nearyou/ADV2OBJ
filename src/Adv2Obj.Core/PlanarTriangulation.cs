namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static List<(int A, int B, int C)> TriangulatePlanarBoundary(List<int> boundary, List<Vertex> vertices, out bool complete)
    {
        complete = false;
        List<(int A, int B, int C)> result = [];
        int count = boundary.Count;
        if (count < 3) return result;
        int[] ids = boundary.AsEnumerable().Reverse().ToArray();
        Vertex normal = new(0, 0, 0);
        for (int i = 0; i < count; i++) normal = Add(normal, Cross(vertices[ids[i]], vertices[ids[(i + 1) % count]]));
        if (Length(normal) < 1e-12) return result;
        int axis = Math.Abs(normal.X) > Math.Abs(normal.Y)
            ? (Math.Abs(normal.X) > Math.Abs(normal.Z) ? 0 : 2)
            : (Math.Abs(normal.Y) > Math.Abs(normal.Z) ? 1 : 2);
        double sign = Math.Sign(Coordinate(normal, axis));
        double[] x = ids.Select(i => Coordinate(vertices[i], (axis + 1) % 3)).ToArray();
        double[] y = ids.Select(i => Coordinate(vertices[i], (axis + 2) % 3)).ToArray();
        double minX = x.Min(), minY = y.Min();
        int resolution = Math.Clamp((int)Math.Sqrt(count), 1, 128);
        double cellX = Math.Max(1e-12, (x.Max() - minX) / resolution);
        double cellY = Math.Max(1e-12, (y.Max() - minY) / resolution);
        int Column(double value) => Math.Clamp((int)((value - minX) / cellX), 0, resolution - 1);
        int Row(double value) => Math.Clamp((int)((value - minY) / cellY), 0, resolution - 1);
        var cells = new Dictionary<(int X, int Y), List<int>>();
        for (int i = 0; i < count; i++)
        {
            var key = (Column(x[i]), Row(y[i]));
            if (!cells.TryGetValue(key, out var items)) cells[key] = items = [];
            items.Add(i);
        }
        int[] previous = Enumerable.Range(0, count).Select(i => (i + count - 1) % count).ToArray();
        int[] next = Enumerable.Range(0, count).Select(i => (i + 1) % count).ToArray();
        bool[] active = Enumerable.Repeat(true, count).ToArray();
        double Turn(int a, int b, int c) => sign * ((x[b] - x[a]) * (y[c] - y[a]) - (y[b] - y[a]) * (x[c] - x[a]));
        bool ContainsPoint(int a, int b, int c)
        {
            double left = Math.Min(x[a], Math.Min(x[b], x[c])) - 1e-8;
            double right = Math.Max(x[a], Math.Max(x[b], x[c])) + 1e-8;
            double bottom = Math.Min(y[a], Math.Min(y[b], y[c])) - 1e-8;
            double top = Math.Max(y[a], Math.Max(y[b], y[c])) + 1e-8;
            for (int col = Column(left); col <= Column(right); col++)
            for (int row = Row(bottom); row <= Row(top); row++)
            {
                if (!cells.TryGetValue((col, row), out var items)) continue;
                foreach (int p in items)
                {
                    if (!active[p] || p == a || p == b || p == c || x[p] < left || x[p] > right || y[p] < bottom || y[p] > top) continue;
                    if (Turn(a, b, p) >= -1e-10 && Turn(b, c, p) >= -1e-10 && Turn(c, a, p) >= -1e-10) return true;
                }
            }
            return false;
        }
        // Local updates and a spatial index avoid repeatedly scanning the full
        // boundary for every ear, particularly on large scan meshes.
        int current = 0, remaining = count, rejected = 0;
        while (remaining > 3)
        {
            int a = previous[current], c = next[current];
            if (Turn(a, current, c) > 1e-10 && !ContainsPoint(a, current, c))
            {
                result.Add((ids[a], ids[current], ids[c]));
                active[current] = false;
                next[a] = c; previous[c] = a;
                remaining--; rejected = 0; current = a;
            }
            else
            {
                current = next[current];
                if (++rejected >= remaining)
                {
                    // Clipping can leave a chain exactly on the intersection
                    // of two planes. Retain its subdivisions with zero-area
                    // triangles instead of leaving unmatched boundary edges.
                    List<int> chain = [current];
                    for (int p = next[current]; p != current; p = next[p]) chain.Add(p);
                    Vertex origin = vertices[ids[current]];
                    Vertex direction = chain.Select(p => Subtract(vertices[ids[p]], origin)).MaxBy(Length);
                    double length = Length(direction);
                    if (length > 0 && chain.All(p => Length(Cross(Subtract(vertices[ids[p]], origin), direction)) / length <= 1e-6))
                    {
                        // An independent center avoids reusing a diagonal that
                        // already belongs to the adjacent surface along this line.
                        int center = vertices.Count;
                        vertices.Add(new Vertex(chain.Average(p => vertices[ids[p]].X),
                            chain.Average(p => vertices[ids[p]].Y), chain.Average(p => vertices[ids[p]].Z)));
                        for (int p = 0; p < chain.Count; p++)
                            result.Add((ids[chain[p]], ids[chain[(p + 1) % chain.Count]], center));
                        complete = true;
                    }
                    return result;
                }
            }
        }
        result.Add((ids[previous[current]], ids[current], ids[next[current]]));
        complete = true;
        return result;
    }
}
