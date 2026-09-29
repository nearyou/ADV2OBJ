using System.Collections;
using System.Reflection;
using Adv2Obj.Core;

internal static class ShiftedMeshChecks
{
    public static void Run()
    {
        CheckNonSphericalScan();
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
        CheckDeclaredRecovery(stream.ToArray(), vertices.Count, faces);
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
        CheckRecoveredSpikeDetection(mesh);
        CheckScanRefinement(stream.ToArray(), payload.ToArray(), vertices, faces, damagedVertex);
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

    private static void CheckRecoveredSpikeDetection(object mesh)
    {
        var check = typeof(AdvToObjConverter).GetMethod("HasSustainedScanSpikes", BindingFlags.Static | BindingFlags.NonPublic)!;
        var meshType = mesh.GetType();
        var source = (IList)meshType.GetProperty("Vertices")!.GetValue(mesh)!;
        object Make(double scale, bool corrupt)
        {
            var points = (IList)Activator.CreateInstance(source.GetType())!;
            for (int i = 0; i < source.Count; i++)
            {
                object v = source[i]!;
                double Axis(string axis) => (double)v.GetType().GetProperty(axis)!.GetValue(v)!;
                double x = Axis("X") * scale, y = Axis("Y") * scale, z = Axis("Z") * scale;
                if (corrupt && i % 64 == 0) { x += 20000 * scale; z -= 15000 * scale; }
                points.Add(Activator.CreateInstance(v.GetType(), x, y, z));
            }
            return Activator.CreateInstance(meshType, points, meshType.GetProperty("Faces")!.GetValue(mesh), 0, new List<string>())!;
        }
        foreach (double scale in new[] { .001, 1.0, 100.0 })
        {
            Require(!(bool)check.Invoke(null, [Make(scale, false)])!, "Valid dense geometry must pass at any unit scale.");
            Require((bool)check.Invoke(null, [Make(scale, true)])!, "Watertight connectivity must not conceal displaced-coordinate spikes.");
        }
        Console.WriteLine("PASS recovered scan geometry: displaced-coordinate spikes detected independently of units and topology.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    private static void CheckNonSphericalScan()
    {
        const int sides = 40;
        var vertices = new List<(double X, double Y, double Z)>();
        var faces = new List<(int A, int B, int C)>();
        for (int a = 0; a < sides; a++)
        for (int b = 0; b < sides; b++)
        {
            double u = a * Math.Tau / sides, v = b * Math.Tau / sides;
            vertices.Add(((1000 + 300 * Math.Cos(v)) * Math.Cos(u),
                (1000 + 300 * Math.Cos(v)) * Math.Sin(u), 500 + 300 * Math.Sin(v)));
            int i = a * sides + b, j = ((a + 1) % sides) * sides + b;
            int k = a * sides + (b + 1) % sides, l = ((a + 1) % sides) * sides + (b + 1) % sides;
            faces.Add((i, j, k)); faces.Add((j, l, k));
        }
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        writer.Write(Convert.FromHexString("95F2B28927963B48A7A200CBA02F27AB"));
        writer.Write(1); writer.Write(8 + vertices.Count * 24 + faces.Count * 16); writer.Write(vertices.Count);
        foreach (var (x, y, z) in vertices) { writer.Write(x); writer.Write(y); writer.Write(z); }
        writer.Write(faces.Count);
        foreach (var (a, b, c) in faces) { writer.Write(3); writer.Write(a); writer.Write(b); writer.Write(c); }
        var decoder = typeof(AdvToObjConverter).GetMethod("DecodeMesh", BindingFlags.Static | BindingFlags.NonPublic)!;
        object mesh = decoder.Invoke(null, [bytes.ToArray()])!;
        int actual = ((IEnumerable)mesh.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>().Count();
        Require(actual == vertices.Count, "A closed scan with a handle must not gain inferred vertices.");
        Console.WriteLine("PASS declared non-spherical topology without invented vertices.");
    }

    private static void CheckDeclaredRecovery(byte[] intact, int vertexCount, List<(int A, int B, int C)> faces)
    {
        Convert.FromHexString("95F2B28927963B48A7A200CBA02F27AB").CopyTo(intact, 12);
        var missing = faces.Select((face, index) => (face, index))
            .Where(f => f.face.A == 500 || f.face.B == 500 || f.face.C == 500)
            .Where((_, index) => index % 2 == 0).Select(f => f.index).ToList();
        var damaged = intact.ToList();
        int faceStart = 44 + vertexCount * 24;
        foreach (int index in missing.OrderDescending()) damaged.RemoveRange(faceStart + index * 16, 16);
        var method = typeof(AdvToObjConverter).GetMethod("TryRecoverDeclaredMesh", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? result = method.Invoke(null, [damaged.ToArray(), CancellationToken.None, false]);
        Require(result is not null, "Counted scan with touching triangle gaps was not recovered.");
        var actual = ((IEnumerable)result!.GetType().GetProperty("Vertices")!.GetValue(result)!).Cast<object>().ToArray();
        Require(actual.Length == vertexCount, "Local gap recovery lost scan points.");
        for (int i = 0; i < vertexCount; i++)
        for (int axis = 0; axis < 3; axis++)
        {
            double expected = BitConverter.ToDouble(intact, 40 + i * 24 + axis * 8);
            double value = (double)actual[i].GetType().GetProperty(new[] { "X", "Y", "Z" }[axis])!.GetValue(actual[i])!;
            Require(value == expected, "Local gap recovery changed an intact coordinate.");
        }
        var recovered = ((IEnumerable)result.GetType().GetProperty("Faces")!.GetValue(result)!).Cast<(int A, int B, int C)>().ToList();
        Require(recovered.Count == faces.Count, "Local gap recovery did not restore declared face count.");
        DecoderChecks.VerifySeparation(recovered, Enumerable.Range(0, vertexCount).ToArray(), recovered);
        byte[] conflicting = intact.ToArray();
        Array.Copy(conflicting, faceStart, conflicting, faceStart + 16, 16);
        Require(method.Invoke(null, [conflicting, CancellationToken.None, false]) is null,
            "Counted recovery accepted conflicting duplicate connectivity.");
        var spatial = typeof(AdvToObjConverter).GetMethod("TriangulateSpatialGap", BindingFlags.Static | BindingFlags.NonPublic)!;
        var occupied = new HashSet<(int, int)> { (0, 700) };
        object?[] arguments = [new List<int> { 0, 500, 700, 900 }, result.GetType().GetProperty("Vertices")!.GetValue(result), occupied, false];
        var patch = (List<(int A, int B, int C)>)spatial.Invoke(null, arguments)!;
        Require((bool)arguments[3]! && patch.Count == 2, "A small spatial gap was not triangulated.");
        var patchEdges = patch.SelectMany(f => new[] { (f.A, f.B), (f.B, f.C), (f.C, f.A) })
            .Select(e => (Math.Min(e.Item1, e.Item2), Math.Max(e.Item1, e.Item2))).ToList();
        Require(!patchEdges.Contains((0, 700)) && patchEdges.Count(e => e == (500, 900)) == 2,
            "Gap recovery reused an occupied diagonal instead of the available connection.");
        occupied.Add((500, 900));
        Require(((List<(int, int, int)>)spatial.Invoke(null, arguments)!).Count == 0 && !(bool)arguments[3]!,
            "A gap with no unoccupied diagonal must be rejected.");
        Console.WriteLine("PASS counted triangle gaps: every coordinate retained, touching gaps closed, declared counts restored, conflicting connectivity rejected.");
    }

    private static void CheckScanRefinement(byte[] intact, byte[] shifted,
        List<(double X, double Y, double Z)> expected, List<(int A, int B, int C)> faces, int damagedVertex)
    {
        var converter = typeof(AdvToObjConverter);
        var readVertex = converter.GetMethod("ReadVertex", BindingFlags.NonPublic | BindingFlags.Static)!;
        var vertexType = readVertex.ReturnType;
        var vertices = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(vertexType))!;
        for (int i = 0; i < expected.Count; i++) vertices.Add(readVertex.Invoke(null, [intact, 40 + 24 * i]));
        var meshType = converter.GetNestedType("Mesh", BindingFlags.NonPublic)!;
        var constructor = meshType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 4);
        object guide = constructor.Invoke([vertices, faces, 0, new List<string>()]);
        var independent = converter.GetMethod("RecoverIndependentScan", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] typed = intact.ToArray();
        Convert.FromHexString("95F2B28927963B48A7A200CBA02F27AB").CopyTo(typed, 12);
        bool[] unknown = new bool[typed.Length];
        object scan = independent.Invoke(null, [typed, unknown, guide, CancellationToken.None])
            ?? throw new Exception("A completely independent source scan was not preserved.");
        var preserved = ((IEnumerable)meshType.GetProperty("Vertices")!.GetValue(scan)!).Cast<object>().ToArray();
        Require(preserved.Length == expected.Count && preserved.SequenceEqual(vertices.Cast<object>()),
            "Independent scan recovery changed a measured coordinate.");
        Require(((IEnumerable)meshType.GetProperty("Faces")!.GetValue(scan)!).Cast<(int, int, int)>().SequenceEqual(faces),
            "Independent scan recovery changed source triangles.");
        unknown[40 + 50 * 24] = true;
        Require(independent.Invoke(null, [typed, unknown, guide, CancellationToken.None]) is null,
            "A dictionary-dependent coordinate was accepted as an independent measurement.");
        unknown[40 + 50 * 24] = false;
        unknown[40 + expected.Count * 24 + 8] = true;
        Require(independent.Invoke(null, [typed, unknown, guide, CancellationToken.None]) is null,
            "A dictionary-dependent triangle index was accepted.");
        Console.WriteLine("PASS independent dense recovery: exact measured coordinates and source topology retained; unknown geometry rejected.");
        var refine = converter.GetMethod("TryRefinePolygonSurface", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? result = refine.Invoke(null, [shifted, 12, guide, CancellationToken.None]);
        Require(result is not null, "Guided scan refinement rejected a recoverable measured surface.");
        Require((bool)meshType.GetProperty("UsesRecoveredScanSurface")!.GetValue(result)!,
            "Recovered scan geometry must carry a review marker.");
        var points = ((IEnumerable)meshType.GetProperty("Vertices")!.GetValue(result)!).Cast<object>().Select(v =>
            ((double)vertexType.GetProperty("X")!.GetValue(v)!,
             (double)vertexType.GetProperty("Y")!.GetValue(v)!,
             (double)vertexType.GetProperty("Z")!.GetValue(v)!)).ToList();
        Require(points.Count >= expected.Count - 3, "Scan refinement discarded intact measured vertices.");
        for (int i = 0; i < expected.Count; i++)
        {
            if (Math.Abs(i - damagedVertex) <= 1) continue;
            var p = expected[i];
            Require(points.Any(q => Math.Abs(q.Item1 - p.X) + Math.Abs(q.Item2 - p.Y)
                + Math.Abs(q.Item3 - p.Z) < 1e-7), "Scan refinement altered an intact measurement.");
        }
        byte[] conflicting = shifted.ToArray();
        int table = 40 + expected.Count * 24 - 8 + 4;
        // Keep record counts intact while supplying unrelated index connections.
        for (int i = 0; i < 1400; i++)
        {
            int a = (i * 17) % expected.Count;
            BitConverter.GetBytes(a).CopyTo(conflicting, table + i * 16 + 4);
            BitConverter.GetBytes((a + 503) % expected.Count).CopyTo(conflicting, table + i * 16 + 8);
            BitConverter.GetBytes((a + 1007) % expected.Count).CopyTo(conflicting, table + i * 16 + 12);
        }
        Require(refine.Invoke(null, [conflicting, 12, guide, CancellationToken.None]) is null,
            "Conflicting source triangles must cause refinement to fall back.");
        Console.WriteLine("PASS guided scan refinement: intact measurements preserved, recovered geometry identified and inconsistent source connections rejected.");
    }
}
