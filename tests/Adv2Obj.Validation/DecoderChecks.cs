using System.Globalization;
using System.IO.Compression;
using System.Text;
using Adv2Obj.Core;

internal static class DecoderChecks
{
    public static async Task RunAsync()
    {
        CheckTouchingSurfaces();
        string temporary = Directory.CreateTempSubdirectory("adv2obj-decoder-").FullName;
        try
        {
            var converter = new AdvToObjConverter();
            string output = Path.Combine(temporary, "output");

            // The mesh is a bipyramid. Metadata before its vertex table can be
            // mistaken for finite doubles; its declared record length is the
            // reliable boundary. Four vertices are replaced by a short marker.
            var vertices = new List<(double X, double Y, double Z)> { (0, 0, 0), (0, 0, 1000) };
            for (int i = 0; i < 12; i++)
                vertices.Add((1000 * Math.Cos(i * Math.PI / 6), 1000 * Math.Sin(i * Math.PI / 6), 500));
            var faces = new List<(int A, int B, int C)>();
            for (int i = 0; i < 12; i++)
            {
                int a = i + 2, b = (i + 1) % 12 + 2;
                faces.Add((0, b, a));
                faces.Add((1, a, b));
            }
            string damagedInput = Path.Combine(temporary, "record-boundary.adv");
            File.WriteAllBytes(damagedInput, BuildAdv(vertices, faces, damaged: true, cut: false));
            ConversionResult damaged = await converter.ConvertAsync(damagedInput, output);
            var actual = ReadVertices(Path.Combine(damaged.OutputDirectory, "record-boundary_Rough.obj"));
            Require(actual.Count == vertices.Count && damaged.FaceCount == faces.Count,
                "The declared mesh record must preserve vertex and face counts.");
            for (int i = 0; i < 5; i++)
                Require(Distance(actual[i], vertices[i]) < 1e-8,
                    "Intact prefix coordinates must come from the declared vertex table.");
            Require(damaged.RepairedVertexCount >= 4 && damaged.Warnings.Count > 0,
                "Reconstructed coordinates must be reported.");
            Console.WriteLine("PASS declared mesh boundary and damaged-record reporting.");

            vertices = [(0, 0, 0), (1000, 0, 0), (1000, 1000, 0), (0, 1000, 0),
                        (0, 0, 1000), (1000, 0, 1000), (1000, 1000, 1000), (0, 1000, 1000)];
            faces = [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7),
                     (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5),
                     (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)];
            byte[] source = BuildAdv(vertices, faces, damaged: false, cut: true);
            string input = Path.Combine(temporary, "plane.adv");
            File.WriteAllBytes(input, source);
            ConversionResult result = await converter.ConvertAsync(input, output);
            var cut = ReadVertices(Path.Combine(result.OutputDirectory, "plane_Saw1-1.obj"));
            Require(Math.Abs(cut.Max(v => 1.005 * v.Z) - 500) < 1e-6
                && Math.Abs(cut.Min(v => 1.005 * v.Z) - 450) < 1e-6,
                "The exported kerf must satisfy the original plane coefficients.");
            Console.WriteLine("PASS cut position and thickness with a non-unit stored normal.");

            string[] symbols = File.ReadAllLines(Path.Combine(result.OutputDirectory, "plane_GalaxySymbols.csv"));
            Require(symbols.SequenceEqual(new[] { "X, 100, 200, 300", "T, 101, 201, 301", "V, 102, 202, 302" }),
                "The declared symbol table must exclude unrelated historical records and K symbols.");
            Console.WriteLine("PASS declared Galaxy table overrides unrelated six-symbol records.");

            string renamed = Path.Combine(temporary, "different-name.adv");
            File.WriteAllBytes(renamed, source);
            ConversionResult renamedResult = await converter.ConvertAsync(renamed, output);
            foreach (string file in Directory.GetFiles(result.OutputDirectory))
            {
                string suffix = Path.GetFileName(file)["plane".Length..];
                string other = Path.Combine(renamedResult.OutputDirectory, "different-name" + suffix);
                Require(File.ReadAllBytes(file).SequenceEqual(File.ReadAllBytes(other)),
                    "Input filenames must not affect exported geometry or companion data.");
            }
            Require(File.Exists(input) && File.Exists(renamed) && File.Exists(damagedInput),
                "Decoder checks must not remove source files.");
            Console.WriteLine("PASS identical data under different input filenames produces identical content.");

            foreach (int firstOrdinal in new[] { 0, 1 })
            {
                string pairStem = "paired-" + firstOrdinal;
                string pairInput = Path.Combine(temporary, pairStem + ".adv");
                File.WriteAllBytes(pairInput, BuildAdv(vertices, faces, damaged: false,
                    cut: true, firstPieOrdinal: firstOrdinal));
                ConversionResult paired = await converter.ConvertAsync(pairInput, output);
                var first = ReadVertices(Path.Combine(paired.OutputDirectory, $"{pairStem}_Pie1-{firstOrdinal}.obj"));
                var second = ReadVertices(Path.Combine(paired.OutputDirectory, $"{pairStem}_Pie1-{firstOrdinal + 1}.obj"));
                Require(Math.Abs(first.Min(v => v.X) - 550) < 1e-6
                    && Math.Abs(first.Max(v => v.X) - 600) < 1e-6
                    && Math.Abs(first.Min(v => v.Y) - 650) < 1e-6
                    && Math.Abs(second.Min(v => v.X) - 550) < 1e-6
                    && Math.Abs(second.Min(v => v.Y) - 650) < 1e-6
                    && Math.Abs(second.Max(v => v.Y) - 700) < 1e-6,
                    "Both numbering conventions must apply the companion plane to the correct Pie mesh.");
            }
            Console.WriteLine("PASS zero-based and one-based Pie pairing.");
            await CheckCutHistory(converter, temporary, output, vertices, faces);
            await CheckPointRecovery(converter, temporary, output);
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static byte[] BuildAdv(List<(double X, double Y, double Z)> vertices,
        List<(int A, int B, int C)> faces, bool damaged, bool cut, int? firstPieOrdinal = null,
        bool truncatedFaces = false)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
        {
            // Arbitrary metadata, deliberately resembling coordinate triples.
            for (int i = 0; i < 15; i++) writer.Write((double)(i + 1));
            writer.Write(checked((uint)(8 + vertices.Count * 24 + faces.Count * 16)));
            writer.Write((uint)vertices.Count);
            for (int i = 0; i < vertices.Count; i++)
            {
                if (damaged && i == 5) writer.Write(Enumerable.Repeat((byte)0xff, 40).ToArray());
                if (damaged && i is >= 5 and < 9) continue;
                writer.Write(vertices[i].X); writer.Write(vertices[i].Y); writer.Write(vertices[i].Z);
            }
            writer.Write((uint)faces.Count);
            foreach (var face in faces)
            {
                writer.Write(3u); writer.Write((uint)face.A); writer.Write((uint)face.B); writer.Write((uint)face.C);
            }
        }
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, true))
        using (Stream entry = archive.CreateEntry("ZippedData", CompressionLevel.Optimal).Open())
            entry.Write(truncatedFaces ? payload.ToArray()[..^8] : payload.ToArray());

        using var source = new MemoryStream();
        using (var writer = new BinaryWriter(source, Encoding.UTF8, true))
        {
            writer.Write(Convert.FromHexString("CA9B28C7D7EC9B45AABFE4423FEF0EFF"));
            writer.Write(new byte[0x34 - 16]); writer.Write(64u); writer.Write(new byte[8]);
            writer.Write(archiveBytes.ToArray());
            if (cut)
            {
                if (firstPieOrdinal is int first)
                {
                    WriteCut(writer, $"Pie1-{first}", 600, (1, 0, 0));
                    WriteCut(writer, $"Pie1-{first + 1}", 700, (0, 1, 0));
                }
                else WriteCut(writer, "Saw1-1", 500, (0, 0, 1.005));
            }
            // A complete counted table precedes unrelated historical records.
            writer.Write(Convert.FromHexString("F36BED43B332FA607EE3551D"));
            writer.Write(new byte[16]); writer.Write(3u);
            for (int i = 0; i < 3; i++) WriteSymbol(writer, i, i, 100 + i, 200 + i, 300 + i);
            writer.Write(new byte[128]);
            for (int i = 0; i < 7; i++) WriteSymbol(writer, i + 10, i, 400 + i, 500 + i, 600 + i);
        }
        return source.ToArray();
    }

    private static void WriteSymbol(BinaryWriter writer, int code, int ordinal, double x, double y, double z)
    {
        writer.Write((uint)code); writer.Write((uint)ordinal); writer.Write(2u); writer.Write(1u);
        writer.Write(new byte[24]); writer.Write(x); writer.Write(y); writer.Write(z); writer.Write(new byte[16]);
    }

    private sealed record TestCut(string Name, double Distance, (double X, double Y, double Z) Normal, uint Branch = 0);

    private static void WriteGroup(BinaryWriter writer, int piece, params TestCut[] cuts)
    {
        writer.Write(Convert.FromHexString("A0C08CABAB842A4692BCD35D2AFAADCB"));
        writer.Write(0xbeb00004u);
        writer.Write(12 + cuts.Sum(cut => 106 + cut.Name.Length));
        writer.Write(cuts.Length);
        foreach (TestCut cut in cuts)
        {
            writer.Write(Convert.FromHexString("2865304F9501CB43B95997C5EA20E0F3"));
            writer.Write(1); writer.Write(78 + cut.Name.Length); writer.Write(123);
            writer.Write(50.0); writer.Write(cut.Distance); writer.Write(0.0);
            writer.Write(cut.Normal.X); writer.Write(cut.Normal.Y); writer.Write(cut.Normal.Z);
            writer.Write(1); writer.Write(cut.Name.Length); writer.Write(Encoding.ASCII.GetBytes(cut.Name));
            writer.Write(new byte[9]); writer.Write(Encoding.ASCII.GetBytes("Allow")); writer.Write(1);
            writer.Write(cut.Branch);
        }
        writer.Write(-1); writer.Write(piece);
    }

    private static async Task CheckCutHistory(AdvToObjConverter converter, string temporary, string output,
        List<(double X, double Y, double Z)> vertices, List<(int A, int B, int C)> faces)
    {
        using var data = new MemoryStream();
        data.Write(BuildAdv(vertices, faces, damaged: false, cut: false));
        using (var writer = new BinaryWriter(data, Encoding.UTF8, true))
        {
            // The flat list can have a different order from the grouped export.
            WriteCut(writer, "Saw1-4", 500, (0, 0, 1));
            WriteCut(writer, "Pie1-1", 600, (1, 0, 0));
            WriteCut(writer, "Pie1-2", 700, (0, 1, 0));
            WriteCut(writer, "Saw1-1", 800, (0, 0, 1));
            WriteCut(writer, "Saw1-2", 300, (1, 0, 0));
            WriteCut(writer, "Saw1-3", 300, (0, 1, 0));
            WriteGroup(writer, 7, new("Pie1-1", 600, (1, 0, 0)), new("Pie1-2", 700, (0, 1, 0)));
            WriteGroup(writer, 0, new("Saw1-1", 800, (0, 0, 1)),
                new("Saw1-2", 300, (1, 0, 0), 1), new("Saw1-3", 300, (0, 1, 0), 2));
            WriteGroup(writer, 7, new TestCut("Saw1-4", 500, (0, 0, 1)));
        }
        string path = Path.Combine(temporary, "history.adv");
        File.WriteAllBytes(path, data.ToArray());
        var result = await converter.ConvertAsync(path, output);
        Require(File.ReadAllLines(Path.Combine(result.OutputDirectory, "history_SawsMD.ini")).Skip(2)
            .Select(line => line.Split(' ')[0]).SequenceEqual(new[] { "Pie1-1", "Pie1-2", "Saw1-1", "Saw1-2", "Saw1-3", "Saw1-4" }),
            "The INI must use serialized group order rather than flat-list order or filenames.");
        foreach (var (name, expectedVolume) in new[] { ("Saw1-1", 42_125_000.0), ("Saw1-2", 10_000_000.0),
                     ("Saw1-3", 37_500_000.0), ("Saw1-4", 6_000_000.0) })
        {
            string obj = Path.Combine(result.OutputDirectory, "history_" + name + ".obj");
            var points = ReadVertices(obj);
            var triangles = ReadFaces(obj);
            VerifySeparation(triangles, Enumerable.Range(0, points.Count).ToArray(), triangles);
            Require(Math.Abs(ObjVolume(points, triangles) - expectedVolume) < .01,
                name + " must follow its source piece and ancestor cuts, with a closed concave cap.");
        }
        string renamed = Path.Combine(temporary, "renamed-history.adv");
        File.WriteAllBytes(renamed, data.ToArray());
        var renamedResult = await converter.ConvertAsync(renamed, output);
        foreach (string obj in Directory.GetFiles(result.OutputDirectory))
            Require(File.ReadAllBytes(obj).SequenceEqual(File.ReadAllBytes(Path.Combine(renamedResult.OutputDirectory,
                "renamed-history" + Path.GetFileName(obj)["history".Length..]))), "Cut history must be independent of the filename.");
        Console.WriteLine("PASS source-piece history, both saw branches, concave caps, and renamed input.");
    }

    private static async Task CheckPointRecovery(AdvToObjConverter converter, string temporary, string output)
    {
        const int rings = 32, sectors = 32;
        var vertices = new List<(double X, double Y, double Z)> { (0, 0, 1000), (0, 0, -1000) };
        for (int r = 1; r < rings; r++)
        for (int s = 0; s < sectors; s++)
            vertices.Add((1000 * Math.Sin(r * Math.PI / rings) * Math.Cos(s * Math.Tau / sectors),
                1000 * Math.Sin(r * Math.PI / rings) * Math.Sin(s * Math.Tau / sectors), 1000 * Math.Cos(r * Math.PI / rings)));
        var faces = new List<(int A, int B, int C)>();
        for (int s = 0; s < sectors; s++) faces.Add((0, 2 + s, 2 + (s + 1) % sectors));
        for (int r = 0; r < rings - 2; r++)
        for (int s = 0; s < sectors; s++)
        {
            int a = 2 + r * sectors + s, b = 2 + r * sectors + (s + 1) % sectors;
            faces.Add((a, a + sectors, b)); faces.Add((b, a + sectors, b + sectors));
        }
        for (int s = 0; s < sectors; s++) faces.Add((1, 2 + (rings - 2) * sectors + (s + 1) % sectors, 2 + (rings - 2) * sectors + s));
        string path = Path.Combine(temporary, "partial-triangles.adv");
        File.WriteAllBytes(path, BuildAdv(vertices, faces, damaged: false, cut: false, truncatedFaces: true));
        var result = await converter.ConvertAsync(path, output);
        string obj = Path.Combine(result.OutputDirectory, "partial-triangles_Rough.obj");
        var actual = ReadVertices(obj); var triangles = ReadFaces(obj);
        Require(actual.Count == vertices.Count && actual.Zip(vertices).All(pair => Distance(pair.First, pair.Second) < 1e-8),
            "Point recovery must preserve every measured coordinate in input order.");
        VerifySeparation(triangles, Enumerable.Range(0, actual.Count).ToArray(), triangles);
        Require(result.Warnings.Any(warning => warning.Contains("triangles are reconstructed")),
            "Recovered connectivity must be explicitly reported.");
        Console.WriteLine("PASS incomplete triangle table recovery preserves measured points and reports reconstruction.");
    }

    private static List<(int A, int B, int C)> ReadFaces(string path) => File.ReadLines(path)
        .Where(line => line.StartsWith("f ", StringComparison.Ordinal)).Select(line => line.Split(' '))
        .Select(f => (int.Parse(f[1]) - 1, int.Parse(f[2]) - 1, int.Parse(f[3]) - 1)).ToList();

    private static double ObjVolume(List<(double X, double Y, double Z)> points, List<(int A, int B, int C)> faces)
    {
        double volume = 0;
        foreach (var (i, j, k) in faces)
        {
            var a = points[i]; var b = points[j]; var c = points[k];
            volume += a.X * (b.Y * c.Z - b.Z * c.Y) + a.Y * (b.Z * c.X - b.X * c.Z) + a.Z * (b.X * c.Y - b.Y * c.X);
        }
        return Math.Abs(volume / 6);
    }

    private static void WriteCut(BinaryWriter writer, string name, double distance,
        (double X, double Y, double Z) normal)
    {
        writer.Write(new byte[16]);
        writer.Write(50.0); writer.Write(distance); writer.Write(new byte[16]);
        writer.Write(normal.X); writer.Write(normal.Y); writer.Write(normal.Z); writer.Write(new byte[8]);
        writer.Write(Encoding.ASCII.GetBytes(name)); writer.Write(new byte[16]);
    }

    private static List<(double X, double Y, double Z)> ReadVertices(string path) =>
        File.ReadLines(path).Where(line => line.StartsWith("v ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(f => (double.Parse(f[1], CultureInfo.InvariantCulture),
                double.Parse(f[2], CultureInfo.InvariantCulture), double.Parse(f[3], CultureInfo.InvariantCulture))).ToList();

    private static double Distance((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void CheckTouchingSurfaces()
    {
        // Two closed tetrahedra meet along one geometric edge. Welding their
        // vertices creates a four-face edge; separating the fans must preserve
        // every original triangle exactly while restoring closed topology.
        List<(int A, int B, int C)> faces =
            [(0, 2, 1), (0, 1, 3), (1, 2, 3), (2, 0, 3),
             (0, 4, 1), (0, 1, 5), (1, 4, 5), (4, 0, 5)];
        var split = TriangleTopology.SeparateTouchingFans(6, faces);
        Require(split.VertexSources.Count == 8, "The two tetrahedra need independent edge endpoints.");
        VerifySeparation(faces, split.VertexSources, split.Faces);
        var intact = TriangleTopology.SeparateTouchingFans(4, faces.Take(4).ToList());
        Require(intact.VertexSources.SequenceEqual(Enumerable.Range(0, 4))
            && intact.Faces.SequenceEqual(faces.Take(4)), "An intact mesh must retain its original indices.");
        Console.WriteLine("PASS touching surfaces are separated without changing triangle geometry.");
    }

    internal static void VerifySeparation(IReadOnlyList<(int A, int B, int C)> original,
        IReadOnlyList<int> sources, IReadOnlyList<(int A, int B, int C)> separated)
    {
        Require(original.Count == separated.Count, "Surface separation changed the triangle count.");
        var edges = new Dictionary<(int, int), int>();
        for (int i = 0; i < separated.Count; i++)
        {
            var (a, b, c) = separated[i];
            Require((sources[a], sources[b], sources[c]) == original[i],
                "Surface separation moved or reversed a triangle.");
            foreach (var (x, y) in new[] { (a, b), (b, c), (c, a) })
            {
                var key = x < y ? (x, y) : (y, x);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }
        Require(edges.Values.All(count => count == 2), "Separated surfaces must have two triangles per edge.");
    }
}
