using System.Collections;
using System.Reflection;
using Adv2Obj.Core;

internal static class PolygonMeshChecks
{
    private static readonly byte[] Signature = Convert.FromHexString("95F2B28927963B48A7A200CBA02F27AB");
    private static readonly MethodInfo Recover = typeof(AdvToObjConverter).GetMethod(
        "TryRecoverPolygonMesh", BindingFlags.NonPublic | BindingFlags.Static)!;

    public static void Run()
    {
        var (vertices, polygons) = MakeTruncatedBipyramid();
        byte[] intact = Payload(vertices, polygons);
        object complete = Decode(intact) ?? throw new Exception("Stored polygon pair was not recovered.");
        CheckGeometry(complete, vertices);
        CheckBoundedRecovery(vertices, polygons);
        CheckMaskedPolygonRecovery();
        Require((bool)complete.GetType().GetProperty("UsesCoarseSurface")!.GetValue(complete)!,
            "Polygon fallback must be identified as a coarse surface.");

        var reflected = vertices.ToList();
        foreach (int i in polygons[0]) reflected[i] = reflected[i] with { Z = -reflected[i].Z };
        object repaired = Decode(Payload(reflected, polygons)) ?? throw new Exception("Polygon cap was not repaired.");
        CheckGeometry(repaired, vertices);
        Require((int)repaired.GetType().GetProperty("RepairedVertexCount")!.GetValue(repaired)! == polygons[0].Length,
            "Cap coordinate repairs must be reported.");

        var invalid = vertices.ToList();
        invalid[10] = invalid[10] with { X = double.NaN };
        object repairedCoordinate = Decode(Payload(invalid, polygons))
            ?? throw new Exception("Incident source planes did not repair an invalid coordinate.");
        CheckGeometry(repairedCoordinate, vertices);

        var overwritten = vertices.ToList();
        foreach (int i in polygons[0])
        {
            ulong bits = (ulong)BitConverter.DoubleToInt64Bits(overwritten[i].Z);
            overwritten[i] = overwritten[i] with
            {
                Z = BitConverter.Int64BitsToDouble((long)((bits & 0x00ff_ffff_ffff_ffff) | 0x6900_0000_0000_0000)),
            };
        }
        var erasedIndices = polygons.Select(p => p.Select(i => polygons[0].Contains(i) ? i | 0xee00 : i).ToArray()).ToList();
        object repairedBytes = Decode(Payload(overwritten, erasedIndices))
            ?? throw new Exception("A uniquely supported coordinate-byte repair was not recovered.");
        CheckGeometry(repairedBytes, vertices);

        byte[] disagreeing = intact.ToArray();
        int recordLength = 24 + 38 * vertices.Count + 16;
        BitConverter.GetBytes(12345.0).CopyTo(disagreeing, recordLength + 28);
        Require(Decode(disagreeing) is null, "Disagreeing polygon copies must be rejected.");
        Require(Decode(intact[..recordLength]) is null, "An unpaired polygon record must be rejected.");
        Require(Decode(intact[..^20]) is null, "An unbound polygon pair must be rejected.");

        var collapsed = vertices.Select((v, i) => i % 3 == 0 ? new Point(0, 0, 2000) : v).ToList();
        Require(Decode(Payload(collapsed, polygons)) is null,
            "A point hull that loses a substantial part of the stored surface must be rejected.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { Recover.Invoke(null, [intact, cancellation.Token]); throw new Exception("Polygon recovery ignored cancellation."); }
        catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException) { }
        Console.WriteLine("PASS stored polygon recovery: source coordinates, cap repair, coordinate-byte repair, incident-plane repair, duplicate agreement, truncation rejection, geometry rejection and cancellation.");
    }

    private static object? Decode(byte[] payload) => Recover.Invoke(null, [payload, CancellationToken.None]);

    private static void CheckBoundedRecovery(List<Point> vertices, List<int[]> polygons)
    {
        var method = typeof(AdvToObjConverter).GetMethod("TryRecoverStoredPolygon", BindingFlags.NonPublic | BindingFlags.Static)!;
        byte[] Bounded(List<Point> points, List<int[]> references)
        {
            byte[] pair = Payload(points, references);
            int denseHeader = 2 * (24 + 38 * vertices.Count + 16) + 1;
            const int count = 1024, faces = 2044;
            byte[] data = new byte[denseHeader + 28 + count * 24 + 4 + faces * 16];
            pair.AsSpan(0, denseHeader + 20).CopyTo(data);
            BitConverter.GetBytes(56 * count - 56).CopyTo(data, denseHeader + 20);
            BitConverter.GetBytes(count).CopyTo(data, denseHeader + 24);
            BitConverter.GetBytes(faces).CopyTo(data, denseHeader + 28 + count * 24);
            BitConverter.GetBytes(3).CopyTo(data, denseHeader + 32 + count * 24);
            // Both polygon headers have damaged lengths; the following typed
            // records still bound the data and corroborate the vertex counts.
            BitConverter.GetBytes(int.MaxValue).CopyTo(data, 20);
            BitConverter.GetBytes(int.MaxValue).CopyTo(data, (denseHeader - 1) / 2 + 20);
            return data;
        }
        object? Read(byte[] data) => method.Invoke(null, [data, CancellationToken.None]);
        CheckGeometry(Read(Bounded(vertices, polygons)) ?? throw new Exception("Independent polygon bounds were not recovered."), vertices);
        var damaged = vertices.ToList();
        foreach (int i in polygons[0]) damaged[i] = damaged[i] with { Z = double.NaN };
        var badReferences = polygons.Select(p => p.Select(i => i == 0 ? 128 : i).ToArray()).ToList();
        object repaired = Read(Bounded(damaged, polygons)) ?? throw new Exception("Corroborated polygon coordinates were not recovered.");
        CheckGeometry(repaired, vertices);
        CheckGeometry(Read(Bounded(vertices, badReferences)) ?? throw new Exception("Corroborated polygon references were not recovered."), vertices);
        Require((int)repaired.GetType().GetProperty("RepairedVertexCount")!.GetValue(repaired)! > 0,
            "Bounded polygon coordinate repairs must be reported.");
        var invalid = polygons.Select(p => p.Select(_ => 0).ToArray()).ToList();
        Require(Read(Bounded(vertices, invalid)) is null, "Unverifiable polygon planes must not be accepted.");
        byte[] bounded = Bounded(vertices, polygons);
        var exclude = typeof(AdvToObjConverter).GetMethod("ExcludeUnknownPolygonData", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool[] unknown = new bool[bounded.Length];
        unknown[28 + 10 * 24] = true;
        Require((bool)exclude.Invoke(null, [bounded, unknown])! && double.IsNaN(BitConverter.ToDouble(bounded, 28 + 10 * 24)),
            "Dictionary-dependent coordinates must be marked unknown even when decoded bytes look plausible.");
        CheckGeometry(Read(bounded) ?? throw new Exception("Known polygon planes could not repair an excluded coordinate."), vertices);
        unknown[0] = true;
        Require(!(bool)exclude.Invoke(null, [bounded, unknown])!, "A dictionary-dependent type signature must be rejected.");

        bounded = Bounded(vertices, polygons);
        using var compressed = new MemoryStream();
        compressed.WriteByte(6); // Invalid initial block, followed by a valid block.
        using (var deflater = new System.IO.Compression.DeflateStream(compressed, System.IO.Compression.CompressionLevel.Optimal, true))
            deflater.Write(bounded);
        byte[] zip = compressed.ToArray();
        byte[] container = new byte[96 + zip.Length];
        BitConverter.GetBytes(64).CopyTo(container, 52);
        BitConverter.GetBytes((ushort)8).CopyTo(container, 64);
        BitConverter.GetBytes(zip.Length).CopyTo(container, 74);
        BitConverter.GetBytes(bounded.Length).CopyTo(container, 78);
        BitConverter.GetBytes((ushort)10).CopyTo(container, 82);
        System.Text.Encoding.ASCII.GetBytes("ZippedData").CopyTo(container, 86);
        // ZippedData is ten bytes; the payload follows at offset 96.
        zip.CopyTo(container, 96);
        var restart = typeof(AdvToObjConverter).GetMethod("RecoverCompressedStoredSurface", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] restartArgs = [container, 64, CancellationToken.None, true];
        object recoveredBlock = restart.Invoke(null, restartArgs) ?? throw new Exception("A verified polygon surface after a broken DEFLATE block was not recovered.");
        CheckGeometry(recoveredBlock, vertices);
        Require((bool)recoveredBlock.GetType().GetProperty("UsesCoarseSurface")!.GetValue(recoveredBlock)!,
            "Restarted compressed geometry must retain its coarse-review marker.");
        Console.WriteLine("PASS independently bounded polygon records: coordinate/reference repairs, exact source geometry, and rejection of unsupported planes.");
    }

    private static void CheckGeometry(object mesh, List<Point> expected)
    {
        var actual = ((IEnumerable)mesh.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>().Select(v =>
            new Point((double)v.GetType().GetProperty("X")!.GetValue(v)!,
                (double)v.GetType().GetProperty("Y")!.GetValue(v)!,
                (double)v.GetType().GetProperty("Z")!.GetValue(v)!)).ToList();
        Require(actual.Count == expected.Count, "Polygon recovery discarded a valid source vertex.");
        foreach (Point p in expected)
            Require(actual.Min(q => Length(Subtract(p, q))) < 1e-7, "Polygon recovery changed source geometry.");
        var faces = ((IEnumerable)mesh.GetType().GetProperty("Faces")!.GetValue(mesh)!).Cast<object>().Count();
        Require(faces == 2 * actual.Count - 4, "Recovered polygon surface is not closed.");
    }

    private static byte[] Payload(List<Point> vertices, List<int[]> polygons)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        for (int copy = 0; copy < 2; copy++)
        {
            writer.Write(Signature); writer.Write(1);
            writer.Write(38 * vertices.Count + 16); writer.Write(vertices.Count);
            foreach (Point p in vertices) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            writer.Write(polygons.Count);
            foreach (int[] polygon in polygons)
            {
                writer.Write(polygon.Length);
                foreach (int index in polygon) writer.Write(index | unchecked((int)0xabcd0000));
            }
        }
        writer.Write((byte)1); writer.Write(Signature); writer.Write(1);
        writer.Write(56 * vertices.Count * 3 - 56); writer.Write(vertices.Count * 3);
        writer.Write(0);
        return stream.ToArray();
    }

    // A synthetic convex solid with mixed polygon sizes and a small planar cap.
    // It exercises the serialized V/E/F relation without depending on ADV samples.
    private static (List<Point>, List<int[]>) MakeTruncatedBipyramid(int sectors = 32)
    {
        var original = new List<Point> { new(0, 0, -1000), new(0, 0, 1000) };
        for (int i = 0; i < sectors; i++) original.Add(new(1000 * Math.Cos(2 * Math.PI * i / sectors),
            1000 * Math.Sin(2 * Math.PI * i / sectors), 0));
        var triangles = new List<(int A, int B, int C)>();
        for (int i = 0; i < sectors; i++)
        {
            int a = i + 2, b = (i + 1) % sectors + 2;
            triangles.Add((0, b, a)); triangles.Add((1, a, b));
        }
        var neighbours = Enumerable.Range(0, original.Count).Select(_ => new HashSet<int>()).ToArray();
        foreach (var (a, b, c) in triangles)
        {
            neighbours[a].UnionWith([b, c]); neighbours[b].UnionWith([a, c]); neighbours[c].UnionWith([a, b]);
        }
        var vertices = new List<Point>();
        var edgePoints = new Dictionary<(int, int), int>();
        for (int a = 0; a < original.Count; a++)
        {
            Point normal = Unit(original[a]);
            double depth = neighbours[a].Min(b => Dot(normal, Subtract(original[a], original[b]))) * .2;
            foreach (int b in neighbours[a])
            {
                double t = depth / Dot(normal, Subtract(original[a], original[b]));
                edgePoints[(a, b)] = vertices.Count;
                vertices.Add(Add(original[a], Multiply(Subtract(original[b], original[a]), t)));
            }
        }
        var polygons = new List<int[]>();
        foreach (var (a, b, c) in triangles)
            polygons.Add([edgePoints[(a, b)], edgePoints[(b, a)], edgePoints[(b, c)],
                edgePoints[(c, b)], edgePoints[(c, a)], edgePoints[(a, c)]]);
        for (int a = 0; a < original.Count; a++)
        {
            Point normal = Unit(original[a]);
            Point u = Unit(Cross(normal, Math.Abs(normal.Z) < .9 ? new(0, 0, 1) : new(1, 0, 0)));
            Point v = Cross(normal, u);
            polygons.Add(neighbours[a].Select(b => edgePoints[(a, b)]).OrderBy(i =>
                Math.Atan2(Dot(Subtract(vertices[i], original[a]), v), Dot(Subtract(vertices[i], original[a]), u))).ToArray());
        }
        Point z = Unit(Cross(Subtract(vertices[polygons[0][1]], vertices[polygons[0][0]]),
            Subtract(vertices[polygons[0][2]], vertices[polygons[0][0]])));
        Point x = Unit(Cross(new(0, 1, 0), z)), y = Cross(z, x);
        vertices = vertices.Select(p => new Point(Dot(p, x), Dot(p, y), Dot(p, z) + 2000)).ToList();
        double level = polygons[0].Average(i => vertices[i].Z);
        foreach (int i in polygons[0]) vertices[i] = vertices[i] with { Z = level };
        Require(vertices.Count == polygons.Count * 2 - 4, "Invalid synthetic polygon topology.");
        return (vertices, polygons);
    }

    private static void CheckMaskedPolygonRecovery()
    {
        var (vertices, polygons) = MakeTruncatedBipyramid(64);
        var method = typeof(AdvToObjConverter).GetMethod("TryRecoverPairedPolygonSurface", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? Read(byte[] data) => method.Invoke(null, [data, CancellationToken.None]);
        byte[] Mask(List<Point> points)
        {
            byte[] data = Payload(points, polygons);
            int length = 40 + points.Count * 38;
            const uint marker = 0x975631e7;
            for (int copy = 0; copy < 2; copy++)
            {
                int header = copy * length;
                BitConverter.GetBytes(marker).CopyTo(data, header + 16);
                BitConverter.GetBytes(marker & 0xffffff00 | (uint)(points.Count & 255)).CopyTo(data, header + 24);
                int cursor = header + 32 + points.Count * 24;
                foreach (int[] face in polygons)
                {
                    foreach (int i in Enumerable.Range(0, face.Length))
                    {
                        uint value = face[i] == 0 ? 0u : face[i] == 1 ? marker : marker & 0xffffff00 | (uint)(face[i] & 255);
                        BitConverter.GetBytes(value).CopyTo(data, cursor + 4 + i * 4);
                    }
                    cursor += 4 + face.Length * 4;
                }
            }
            return data;
        }
        byte[] masked = Mask(vertices);
        CheckGeometry(Read(masked) ?? throw new Exception("Upper-byte index recovery failed."), vertices);
        var reflected = vertices.ToList();
        foreach (int i in polygons[0]) reflected[i] = reflected[i] with { Z = -reflected[i].Z };
        CheckGeometry(Read(Mask(reflected)) ?? throw new Exception("Masked cap sign was not recovered."), vertices);
        var smallCap = vertices.ToList();
        foreach (int i in polygons[0]) smallCap[i] = smallCap[i] with { Z = 1e-7 };
        CheckGeometry(Read(Mask(smallCap)) ?? throw new Exception("A finite but incorrect cap height was not recovered from coplanarity."), vertices);
        byte[] brokenSize = masked.ToArray();
        BitConverter.GetBytes(0).CopyTo(brokenSize, 32 + vertices.Count * 24);
        CheckGeometry(Read(brokenSize) ?? throw new Exception("A damaged polygon size discarded the remaining source planes."), vertices);
        byte[] unsupported = masked.ToArray();
        Array.Clear(unsupported, 32 + vertices.Count * 24, 12 * vertices.Count);
        Require(Read(unsupported) is null, "Unsupported polygon geometry must not pass through index recovery.");

        byte[] Container(byte[] primary, byte[] copy)
        {
            using var stream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var (name, data) in new[] { ("ZippedData", primary), ("StoredRough", copy) })
                {
                    using var entry = archive.CreateEntry(name).Open();
                    entry.Write(data); entry.Write(new byte[60000]);
                }
            }
            byte[] result = new byte[64 + stream.Length];
            BitConverter.GetBytes(64).CopyTo(result, 52); stream.ToArray().CopyTo(result, 64);
            return result;
        }
        var corroborate = typeof(AdvToObjConverter).GetMethod("RecoverCorroboratedRoughCopy", BindingFlags.Static | BindingFlags.NonPublic)!;
        CheckGeometry(corroborate.Invoke(null, [Container(masked, masked), masked, CancellationToken.None])
            ?? throw new Exception("An independently corroborated rough copy was rejected."), vertices);
        byte[] unrelated = Mask(vertices.Select(v => v with { X = v.X + 125 }).ToList());
        Require(corroborate.Invoke(null, [Container(masked, unrelated), masked, CancellationToken.None]) is null,
            "A same-count unrelated mesh must not replace the primary rough.");
        Console.WriteLine("PASS masked polygon indices: >256 vertices, exact retained coordinates, cap recovery, damaged-size recovery, unsupported planes rejected, and unrelated cached mesh rejected.");
    }

    private readonly record struct Point(double X, double Y, double Z);
    private static Point Add(Point a, Point b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Point Subtract(Point a, Point b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Point Multiply(Point a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    private static double Dot(Point a, Point b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    private static Point Cross(Point a, Point b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Length(Point a) => Math.Sqrt(Dot(a, a));
    private static Point Unit(Point a) => Multiply(a, 1 / Length(a));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
