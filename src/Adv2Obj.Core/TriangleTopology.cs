namespace Adv2Obj.Core;

internal static class TriangleTopology
{
    // Returns a source-vertex map and new indices. Every triangle retains its
    // original positions and winding; only independent surface fans are split.
    internal static (List<int> VertexSources, List<(int A, int B, int C)> Faces)
        SeparateTouchingFans(int vertexCount, IReadOnlyList<(int A, int B, int C)> faces)
    {
        var edgeFaces = new Dictionary<(int, int), List<int>>();
        static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);
        void AddEdge(int a, int b, int face)
        {
            var key = Key(a, b);
            if (!edgeFaces.TryGetValue(key, out var incident)) edgeFaces[key] = incident = [];
            incident.Add(face);
        }
        for (int i = 0; i < faces.Count; i++)
        {
            var (a, b, c) = faces[i];
            AddEdge(a, b, i); AddEdge(b, c, i); AddEdge(c, a, i);
        }

        var sources = Enumerable.Range(0, vertexCount).ToList();
        var result = faces.ToList();
        var affected = edgeFaces.Where(pair => pair.Value.Count > 2)
            .SelectMany(pair => new[] { pair.Key.Item1, pair.Key.Item2 }).Distinct().Order().ToArray();
        foreach (int vertex in affected)
        {
            // Adjacency through a manifold edge identifies one surface fan.
            // A shared edge with four faces must not join its two separate fans.
            var adjacency = new Dictionary<int, List<int>>();
            foreach (var (edge, incident) in edgeFaces)
            {
                if (edge.Item1 != vertex && edge.Item2 != vertex) continue;
                foreach (int face in incident) adjacency.TryAdd(face, []);
                if (incident.Count != 2) continue;
                adjacency[incident[0]].Add(incident[1]);
                adjacency[incident[1]].Add(incident[0]);
            }
            var visited = new HashSet<int>();
            bool first = true;
            foreach (int seed in adjacency.Keys.Order())
            {
                if (!visited.Add(seed)) continue;
                int replacement = vertex;
                if (!first)
                {
                    replacement = sources.Count;
                    sources.Add(vertex);
                }
                first = false;
                var queue = new Queue<int>();
                queue.Enqueue(seed);
                while (queue.TryDequeue(out int faceIndex))
                {
                    var (a, b, c) = result[faceIndex];
                    result[faceIndex] = (a == vertex ? replacement : a,
                        b == vertex ? replacement : b, c == vertex ? replacement : c);
                    foreach (int neighbour in adjacency[faceIndex])
                        if (visited.Add(neighbour)) queue.Enqueue(neighbour);
                }
            }
        }
        return (sources, result);
    }
}
