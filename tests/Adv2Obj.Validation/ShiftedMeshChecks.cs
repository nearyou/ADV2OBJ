using System.Collections;
using System.Reflection;
using Adv2Obj.Core;

internal static class ShiftedMeshChecks
{
    public static void Run()
    {
        const int rings = 32, sectors = 64;
        var vertices = new List<(double X, double Y, double Z)> { (0, 0, 0) };
        for (int ring = 1; ring < rings; ring++)
        for (int sector = 0; sector < sectors; sector++)
        {
            double latitude = Math.PI * ring / rings, angle = 2 * Math.PI * sector / sectors;
            vertices.Add((1000 * Math.Sin(latitude) * Math.Cos(angle),
                1000 * Math.Sin(latitude) * Math.Sin(angle), 1000 * (1 - Math.Cos(latitude))));
        }
        int top = vertices.Count;
        vertices.Add((0, 0, 2000));
        var faces = new List<(int A, int B, int C)>();
        for (int sector = 0; sector < sectors; sector++)
            faces.Add((0, 1 + (sector + 1) % sectors, 1 + sector));
        for (int ring = 0; ring < rings - 2; ring++)
        for (int sector = 0; sector < sectors; sector++)
        {
            int a = 1 + ring * sectors + sector, b = 1 + ring * sectors + (sector + 1) % sectors;
            faces.Add((a, b, a + sectors));
            faces.Add((b, b + sectors, a + sectors));
        }
        for (int sector = 0; sector < sectors; sector++)
            faces.Add((top - sectors + sector, top - sectors + (sector + 1) % sectors, top));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[32]);
        writer.Write(8 + vertices.Count * 24 + faces.Count * 16);
        writer.Write(vertices.Count);
        foreach (var (x, y, z) in vertices) { writer.Write(x); writer.Write(y); writer.Write(z); }
        writer.Write(faces.Count);
        foreach (var (a, b, c) in faces) { writer.Write(3); writer.Write(a); writer.Write(b); writer.Write(c); }
        var payload = stream.ToArray().ToList();
        int triangleOffset = 40 + vertices.Count * 24 + 4 + 1500 * 16;
        payload.RemoveRange(triangleOffset + 8, 7);
        const int damagedVertex = 300;
        payload.RemoveRange(40 + damagedVertex * 24 + 8, 8);
        var recovery = typeof(AdvToObjConverter).GetMethod("TryRecoverSegmentedMesh", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? mesh = recovery.Invoke(null, [payload.ToArray(), CancellationToken.None]);
        Require(mesh is not null, "Shifted mesh was not recovered.");
        var actual = ((IEnumerable)mesh!.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>().ToArray();
        Require(actual.Length == vertices.Count, "Shifted recovery lost original vertices.");
        for (int i = 0; i < actual.Length; i++)
        {
            if (Math.Abs(i - damagedVertex) <= 1) continue;
            var expected = vertices[i];
            double X(string name) => (double)actual[i].GetType().GetProperty(name)!.GetValue(actual[i])!;
            double error = Math.Abs(X("X") - expected.X) + Math.Abs(X("Y") - expected.Y) + Math.Abs(X("Z") - expected.Z);
            Require(error < 1e-7, $"Shifted recovery changed intact vertex {i} by {error}.");
        }
        var recoveredFaces = ((IEnumerable)mesh.GetType().GetProperty("Faces")!.GetValue(mesh)!).Cast<object>().Count();
        Require(recoveredFaces == faces.Count, "Shifted recovery did not restore a closed surface.");
        byte[] inconsistent = payload.ToArray();
        int conflictingFace = 40 + vertices.Count * 24 + 4 + 100 * 16 - 8;
        for (int i = 0; i < 3; i++)
            BitConverter.GetBytes(i + 1).CopyTo(inconsistent, conflictingFace + 4 + i * 4);
        Require(recovery.Invoke(null, [inconsistent, CancellationToken.None]) is null,
            "Index-valid but conflicting triangle connectivity must not be accepted.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { recovery.Invoke(null, [payload.ToArray(), cancelled.Token]); throw new Exception("Recovery ignored cancellation."); }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException) { }
        Console.WriteLine("PASS shifted vertex/face records: original vertex numbering and intact coordinates preserved; small hole closed; conflicting topology rejected; cancellation honored.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
