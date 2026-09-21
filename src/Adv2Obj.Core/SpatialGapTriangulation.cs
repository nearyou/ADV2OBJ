namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    // Small scan gaps need not be planar. Minimize the patch's 3D area while
    // prohibiting diagonals already occupied by the retained scan surface.
    private static List<(int A, int B, int C)> TriangulateSpatialGap(List<int> boundary,
        List<Vertex> vertices, HashSet<(int, int)> occupied, out bool complete)
    {
        int[] ids = boundary.AsEnumerable().Reverse().ToArray();
        int n = ids.Length;
        complete = false;
        if (n is < 3 or > 16) return [];
        var costs = new double[n, n];
        var split = new int[n, n];
        bool Allowed(int a, int b) => b == a + 1 || a == 0 && b == n - 1
            || !occupied.Contains((Math.Min(ids[a], ids[b]), Math.Max(ids[a], ids[b])));
        for (int span = 2; span < n; span++)
        for (int a = 0; a + span < n; a++)
        {
            int b = a + span;
            costs[a, b] = double.PositiveInfinity;
            split[a, b] = -1;
            if (!Allowed(a, b)) continue;
            for (int k = a + 1; k < b; k++)
            {
                if (!Allowed(a, k) || !Allowed(k, b)) continue;
                double area = Length(Cross(Subtract(vertices[ids[k]], vertices[ids[a]]),
                    Subtract(vertices[ids[b]], vertices[ids[a]])));
                double cost = costs[a, k] + costs[k, b] + area;
                if (cost < costs[a, b]) { costs[a, b] = cost; split[a, b] = k; }
            }
        }
        if (!double.IsFinite(costs[0, n - 1])) return [];
        var result = new List<(int A, int B, int C)>();
        void Emit(int a, int b)
        {
            if (b <= a + 1) return;
            int k = split[a, b];
            result.Add((ids[a], ids[k], ids[b]));
            Emit(a, k); Emit(k, b);
        }
        Emit(0, n - 1);
        complete = true;
        return result;
    }
}
