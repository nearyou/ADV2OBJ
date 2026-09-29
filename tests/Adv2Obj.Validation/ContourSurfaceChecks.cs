using System.Collections;
using System.Reflection;
using System.IO.Compression;
using Adv2Obj.Core;

internal static class ContourSurfaceChecks
{
    private static readonly MethodInfo Recover = typeof(AdvToObjConverter).GetMethod(
        "RecoverContourSurface", BindingFlags.Static | BindingFlags.NonPublic)!;

    public static void Run()
    {
        byte[] container = new byte[64];
        BitConverter.GetBytes(64).CopyTo(container, 0x34);
        var (payload, offsets, expected) = Build();
        object? Decode(byte[] data, CancellationToken token = default)
        {
            try { return Recover.Invoke(null, [container, data, token]); }
            catch (TargetInvocationException error) { throw error.InnerException!; }
        }
        var mesh = Decode(payload) ?? throw new Exception("Intact counted contours were not recovered.");
        Verify(mesh, expected);

        // Exponent/version bytes can be overwritten while height mantissas and
        // complete XY measurements survive. Preserve every point in that case.
        byte[] masked = [.. payload];
        for (int level = 2; level < offsets.Count; level++)
        {
            int p = offsets[level];
            masked[p + 7] = 0x77;
            masked[p + 10] = masked[p + 14] = 0xaa;
            masked[p + 11] = masked[p + 15] = 0xee;
        }
        Verify(Decode(masked) ?? throw new Exception("Framed contour heights were not recovered."), expected);

        byte[] damagedHeight = [.. masked];
        damagedHeight[offsets[16] + 4] ^= 0x5a;
        Verify(Decode(damagedHeight) ?? throw new Exception("A bounded contour with a damaged height byte was discarded."), expected);
        byte[] damagedVersion = [.. masked];
        damagedVersion[offsets[16] + 10] ^= 0x5a;
        Verify(Decode(damagedVersion) ?? throw new Exception("An exact height and counted boundary did not recover the contour."), expected);
        byte[] unsupportedHeight = [.. damagedHeight];
        unsupportedHeight[offsets[16] + 5] ^= 0x5a;
        Require(Decode(unsupportedHeight) is null,
            "Two damaged height bytes must not establish a level or bypass the maximum-gap check.");

        byte[] oneGap = [.. masked];
        int damaged = offsets[16];
        for (int p = damaged + 16; p < offsets[17]; p += 8) BitConverter.GetBytes(double.NaN).CopyTo(oneGap, p);
        var recovered = Decode(oneGap) ?? throw new Exception("A bounded missing contour was not bridged.");
        Verify(recovered, expected.Where(v => v.Z != 160).ToList());
        string warning = string.Join(" ", (IEnumerable<string>)recovered.GetType().GetProperty("Warnings")!.GetValue(recovered)!);
        Require(warning.Contains("1 unreadable levels bridged"), "Missing contour reconstruction must be disclosed.");

        byte[] largeGap = [.. masked];
        for (int i = 10; i <= 15; i++)
            for (int p = offsets[i] + 16; p < offsets[i + 1]; p += 8) BitConverter.GetBytes(double.NaN).CopyTo(largeGap, p);
        Require(Decode(largeGap) is null, "Large missing spans must not be silently invented.");
        Require(Decode(payload[..offsets[^1]]) is null, "A missing terminal contour must not be capped at the wrong height.");

        byte[] invalid = [.. payload];
        BitConverter.GetBytes(99999).CopyTo(invalid, 4);
        Require(Decode(invalid) is null, "Unbounded contour counts must be rejected.");
        byte[] wrongStep = [.. payload];
        BitConverter.GetBytes(17.123).CopyTo(wrongStep, offsets[1]);
        Require(Decode(wrongStep) is null, "A false height sequence must not select unrelated coordinate bytes.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { Decode(payload, cancellation.Token); throw new Exception("Contour recovery did not cancel."); }
        catch (OperationCanceledException) { }
        CheckStoredCopies();
        CheckShortRecords(container);
        Console.WriteLine("PASS contour measurements, masked framing, bounded gaps, terminal coverage, invalid counts and cancellation.");
    }

    private static void CheckShortRecords(byte[] container)
    {
        var (bytes, offsets, expected) = Build(32, 8);
        var exact = expected.ToHashSet();
        foreach (int missing in new[] { 1, 7, 9, 15, 31 })
        {
            int gap = offsets[16] + 16 + 25 * 16 + 5;
            byte[] shortened = [.. bytes[..gap], .. bytes[(gap + missing)..]];
            object mesh = Recover.Invoke(null, [container, shortened, CancellationToken.None])
                ?? throw new Exception($"A {missing}-byte contour gap lost the established recovery.");
            var vertices = ((IEnumerable)mesh.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>().Select(v =>
                ((double)v.GetType().GetProperty("X")!.GetValue(v)!,
                 (double)v.GetType().GetProperty("Y")!.GetValue(v)!,
                 (double)v.GetType().GetProperty("Z")!.GetValue(v)!)).ToList();
            Require(vertices.Count(v => v.Item3 == 160) >= 122, $"Most of a locally damaged contour should be retained ({missing}-byte gap).");
            Require(vertices.All(exact.Contains), "Byte-gap recovery must not change measured points or accept misaligned coordinates.");
            Require(vertices.Count(v => v.Item3 != 160) == expected.Count(v => v.Z != 160), "Unrelated intact levels must stay unchanged.");
        }
        int start = offsets[16] + 16 + 25 * 16 + 5;
        foreach (bool invalidCount in new[] { false, true })
        {
            int missing = invalidCount ? 9 : 70;
            byte[] unsupported = [.. bytes[..start], .. bytes[(start + missing)..]];
            if (invalidCount) unsupported[offsets[16] + 12] ^= 0x40;
            object mesh = Recover.Invoke(null, [container, unsupported, CancellationToken.None])
                ?? throw new Exception("Rejecting an unsupported local gap must retain the missing-level fallback.");
            var vertices = ((IEnumerable)mesh.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>();
            Require(!vertices.Any(v => (double)v.GetType().GetProperty("Z")!.GetValue(v)! == 160),
                "An oversized byte gap or conflicting count must not establish measured points.");
        }
        Console.WriteLine("PASS short contour gaps: original point coordinates and intact levels preserved across multiple byte alignments.");
    }

    private static void CheckStoredCopies()
    {
        var (full, offsets, expected) = Build(256);
        byte[] prefix = full[..offsets[200]];
        byte[] Container(byte[] other)
        {
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output);
            writer.Write(new byte[64]);
            foreach (byte[] data in new[] { prefix, other })
            {
                using var compressed = new MemoryStream();
                using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) deflate.Write(data);
                writer.Write(0x04034b50); writer.Write((ushort)20); writer.Write((ushort)0); writer.Write((ushort)8);
                writer.Write(0); writer.Write(0); writer.Write((int)compressed.Length); writer.Write(data.Length);
                writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(compressed.ToArray());
            }
            byte[] result = output.ToArray();
            BitConverter.GetBytes(64).CopyTo(result, 0x34);
            return result;
        }
        object? RecoverCopy(byte[] other) => Recover.Invoke(null, [Container(other), prefix, CancellationToken.None]);
        Verify(RecoverCopy(full) ?? throw new Exception("A corroborated complete contour copy was not recovered."), expected);
        byte[] unrelated = [.. full];
        foreach (int p in offsets)
        {
            int count = BitConverter.ToInt32(unrelated, p + 12);
            for (int i = 0; i < count; i++)
                BitConverter.GetBytes(BitConverter.ToDouble(unrelated, p + 16 + i * 16) + 1500).CopyTo(unrelated, p + 16 + i * 16);
        }
        Require(RecoverCopy(unrelated) is null, "An unrelated cached model must not fill missing rough contours.");
        Console.WriteLine("PASS complete contour copy corroboration and rejection of an unrelated stored model.");
    }

    private static (byte[] Bytes, List<int> Offsets, List<(double X, double Y, double Z)> Points) Build(int levels = 32, int density = 1)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0); writer.Write(levels);
        var offsets = new List<int>();
        var points = new List<(double X, double Y, double Z)>();
        for (int level = 0; level < levels; level++)
        {
            offsets.Add((int)stream.Position);
            int n = (level % 2 == 0 ? 16 : 24) * density;
            writer.Write(level * 10.0); writer.Write(1); writer.Write(n);
            for (int i = 0; i < n; i++)
            {
                // A concave outline tests that recovery retains source shape;
                // a convex hull would delete these measured indentations.
                double angle = i * 2 * Math.PI / n;
                double radius = 100 * (1 + .22 * Math.Cos(3 * angle));
                double x = radius * Math.Cos(angle) + level, y = radius * Math.Sin(angle);
                writer.Write(x); writer.Write(y);
                points.Add((x, y, level * 10.0));
            }
        }
        writer.Write(Convert.FromHexString("95F2B28927963B48A7A200CBA02F27AB"));
        return (stream.ToArray(), offsets, points);
    }

    private static void Verify(object mesh, List<(double X, double Y, double Z)> expected)
    {
        var vertices = ((IEnumerable)mesh.GetType().GetProperty("Vertices")!.GetValue(mesh)!).Cast<object>().Select(v =>
            ((double)v.GetType().GetProperty("X")!.GetValue(v)!,
             (double)v.GetType().GetProperty("Y")!.GetValue(v)!,
             (double)v.GetType().GetProperty("Z")!.GetValue(v)!)).ToList();
        Require(vertices.SequenceEqual(expected), "Contour recovery must preserve measured coordinates and concavities.");
        Require((bool)mesh.GetType().GetProperty("UsesContourSurface")!.GetValue(mesh)!, "Contour reconstruction must have a distinct review status.");
        var faces = (List<(int A, int B, int C)>)mesh.GetType().GetProperty("Faces")!.GetValue(mesh)!;
        Require(faces.Count == 2 * vertices.Count - 4, "The contour surface must be a closed genus-zero mesh.");
        var edges = new Dictionary<(int, int), int>();
        foreach (var (a, b, c) in faces)
            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            {
                var key = (Math.Min(edge.Item1, edge.Item2), Math.Max(edge.Item1, edge.Item2));
                edges[key] = edges.GetValueOrDefault(key) + (edge.Item1 < edge.Item2 ? 1 : -1);
            }
        Require(edges.Values.All(v => v == 0), "Adjacent contour triangles must have consistent outward winding.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
