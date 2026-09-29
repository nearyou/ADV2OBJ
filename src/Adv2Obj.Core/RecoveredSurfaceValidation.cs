namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static bool HasSustainedScanSpikes(Mesh mesh)
    {
        if (mesh.Vertices.Count < 1000 || mesh.Faces.Count < 1000) return false;
        var lengths = mesh.Faces.Select(f => Math.Max(Distance(mesh.Vertices[f.A], mesh.Vertices[f.B]),
            Math.Max(Distance(mesh.Vertices[f.B], mesh.Vertices[f.C]), Distance(mesh.Vertices[f.C], mesh.Vertices[f.A])))).Order().ToArray();
        // Robust extent prevents a few corrupted coordinates from increasing
        // their own acceptance threshold. Topological closure alone cannot
        // establish that recovered vertex IDs refer to the right coordinates.
        double[] xs = mesh.Vertices.Select(v => v.X).Order().ToArray();
        double[] ys = mesh.Vertices.Select(v => v.Y).Order().ToArray();
        double[] zs = mesh.Vertices.Select(v => v.Z).Order().ToArray();
        int lo = xs.Length / 100, hi = xs.Length - 1 - lo;
        double scale = Distance(new(xs[lo], ys[lo], zs[lo]), new(xs[hi], ys[hi], zs[hi]));
        double limit = Math.Max(lengths[lengths.Length / 2] * 10, scale * .1);
        return lengths.Count(length => !double.IsFinite(length) || length > limit) > lengths.Length * .01;
    }

    private static Mesh CorrectRecoveredScanSpikes(byte[] payload, Mesh mesh, CancellationToken token)
    {
        if (mesh.UsesCoarseSurface || mesh.UsesContourSurface || !HasSustainedScanSpikes(mesh)) return mesh;
        // Prefer the same counted dense scan aligned against an independent
        // stored surface, preserving detail whenever that alignment validates.
        Mesh? replacement = TryRecoverDeclaredMesh(payload, token, allowShifted: true);
        if (replacement is null || HasSustainedScanSpikes(replacement))
            replacement = TryRecoverStoredPolygon(payload, token);
        if (replacement is null || HasSustainedScanSpikes(replacement)) return mesh;
        replacement.Warnings.Add("Rejected spiked scan coordinates that conflict with triangle adjacency; recovered the surface from independently bounded source records.");
        return replacement;
    }
}
