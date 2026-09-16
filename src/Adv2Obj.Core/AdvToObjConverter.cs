using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using g3;
using gs;

namespace Adv2Obj.Core;

public sealed class AdvToObjConverter
{
    private static readonly byte[] AdvSignature =
        [0xCA, 0x9B, 0x28, 0xC7, 0xD7, 0xEC, 0x9B, 0x45, 0xAA, 0xBF, 0xE4, 0x42, 0x3F, 0xEF, 0x0E, 0xFF];

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZippedDataName = Encoding.ASCII.GetBytes("ZippedData");

    // Sparse bit masks observed in the supplied Advisor data. XOR removes the
    // mask while retaining the original triangle index.
    private static readonly uint[] AdvisorMasks =
    [
        0x00000000, 0x00400000, 0x00408800, 0x40880000,
        0x40000000, 0x40408800, 0x40400000, 0x82000000,
        0x82400000, 0x82407600, 0x00824000, 0x00820000,
    ];

    private static readonly HashSet<uint> FaceMarkers =
        AdvisorMasks.Select(mask => mask ^ 3u).ToHashSet();

    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        byte[] source = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        ValidateAdv(source);

        (byte[] payload, int roughRecordEnd) = DecompressRoughPayload(source);
        Mesh mesh;
        try
        {
            mesh = DecodeMesh(payload);
        }
        catch (AdvFormatException)
        {
            mesh = RepairDamagedMesh(payload);
        }
        List<CutPlane> cuts = DecodeActiveCutPlanes(source, roughRecordEnd);

        Directory.CreateDirectory(outputRoot);
        string stem = Path.GetFileNameWithoutExtension(inputPath);
        string outputDirectory = Path.Combine(outputRoot, stem);
        Directory.CreateDirectory(outputDirectory);

        await WriteObjAtomicallyAsync(
            Path.Combine(outputDirectory, $"{stem}_Rough.obj"),
            mesh,
            "Rough",
            cancellationToken);

        foreach (CutPlane cut in cuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mesh slice = SliceMesh(mesh, cut);
            await WriteObjAtomicallyAsync(
                Path.Combine(outputDirectory, $"{stem}_{cut.Name}.obj"),
                slice,
                cut.Name,
                cancellationToken);
        }

        await WriteSawsIniAsync(
            Path.Combine(outputDirectory, $"{stem}_SawsMD.ini"),
            cuts,
            cancellationToken);
        await WriteGalaxySymbolsAsync(
            Path.Combine(outputDirectory, $"{stem}_GalaxySymbols.csv"),
            source,
            cancellationToken);

        return new ConversionResult(
            inputPath,
            outputDirectory,
            cuts.Count + 1,
            mesh.Vertices.Count,
            mesh.Faces.Count,
            mesh.RepairedVertexCount,
            mesh.Warnings);
    }

    private static void ValidateAdv(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || !data[..AdvSignature.Length].SequenceEqual(AdvSignature))
        {
            throw new AdvFormatException("The file is not a supported Sarine Advisor ADV file.");
        }
    }

    private static (byte[] Payload, int RecordEnd) DecompressRoughPayload(byte[] source)
    {
        int metadataOffset = checked((int)ReadUInt32(source, 0x34));
        int searchEnd = Math.Min(source.Length, metadataOffset + 1024);
        int localHeader = FindBytes(source, ZipSignature, metadataOffset, searchEnd);

        int compressedSize;
        int uncompressedSize;
        int dataStart;
        if (localHeader >= 0)
        {
            EnsureRange(source, localHeader, 30);
            ushort method = ReadUInt16(source, localHeader + 8);
            if (method != 8)
            {
                throw new AdvFormatException($"Unsupported embedded compression method: {method}.");
            }

            compressedSize = checked((int)ReadUInt32(source, localHeader + 18));
            uncompressedSize = checked((int)ReadUInt32(source, localHeader + 22));
            int nameLength = ReadUInt16(source, localHeader + 26);
            int extraLength = ReadUInt16(source, localHeader + 28);
            dataStart = checked(localHeader + 30 + nameLength + extraLength);
        }
        else
        {
            // Some Advisor files omit the first eight bytes of the local ZIP
            // header. The compression-method field and the rest are intact.
            int nameOffset = FindBytes(source, ZippedDataName, metadataOffset, searchEnd);
            if (nameOffset < 0)
            {
                throw new AdvFormatException("The embedded rough-mesh payload was not found.");
            }

            int methodOffset = nameOffset - 22;
            EnsureRange(source, methodOffset, 22);
            ushort method = ReadUInt16(source, methodOffset);
            if (method != 8)
            {
                throw new AdvFormatException("The embedded rough-mesh header is damaged.");
            }

            compressedSize = checked((int)ReadUInt32(source, methodOffset + 10));
            uncompressedSize = checked((int)ReadUInt32(source, methodOffset + 14));
            int nameLength = ReadUInt16(source, methodOffset + 18);
            int extraLength = ReadUInt16(source, methodOffset + 20);
            dataStart = checked(methodOffset + 22 + nameLength + extraLength);
        }

        EnsureRange(source, dataStart, compressedSize);
        byte[] compressedData = source.AsSpan(dataStart, compressedSize).ToArray();
        byte[]? payload = TryInflate(compressedData);
        if (payload is not null)
        {
            return (payload, checked(dataStart + compressedSize));
        }

        int failureOffset = FindInflateFailureOffset(compressedData);
        byte[]? bestCandidate = null;
        int bestLengthDifference = int.MaxValue;
        int first = Math.Max(0, failureOffset - 512);
        int last = Math.Min(compressedData.Length - 1, failureOffset + 32);
        for (int offset = first; offset <= last; offset++)
        {
            byte original = compressedData[offset];
            for (int bit = 0; bit < 8; bit++)
            {
                compressedData[offset] = (byte)(original ^ (1 << bit));
                payload = TryInflate(compressedData);
                if (payload is not null && Math.Abs(payload.Length - uncompressedSize) <= 4096)
                {
                    int difference = Math.Abs(payload.Length - uncompressedSize);
                    if (difference < bestLengthDifference)
                    {
                        bestCandidate = payload;
                        bestLengthDifference = difference;
                    }
                    try
                    {
                        _ = DecodeMesh(payload);
                        return (payload, checked(dataStart + compressedSize));
                    }
                    catch (AdvFormatException)
                    {
                        // Structurally valid DEFLATE can still be the wrong repair.
                    }
                }
            }
            compressedData[offset] = original;
        }

        if (bestCandidate is not null)
        {
            return (bestCandidate, checked(dataStart + compressedSize));
        }

        throw new AdvFormatException("The embedded rough-mesh stream could not be decompressed.");
    }

    private static byte[]? TryInflate(byte[] compressedData)
    {
        try
        {
            using var compressed = new MemoryStream(compressedData, false);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var result = new MemoryStream();
            deflate.CopyTo(result);
            return result.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static int FindInflateFailureOffset(byte[] compressedData)
    {
        using var memory = new MemoryStream(compressedData, false);
        using var throttled = new ThrottledReadStream(memory);
        try
        {
            using var deflate = new DeflateStream(throttled, CompressionMode.Decompress);
            Span<byte> buffer = stackalloc byte[4096];
            while (deflate.Read(buffer) > 0) { }
        }
        catch (InvalidDataException)
        {
            return checked((int)memory.Position - 1);
        }
        return compressedData.Length - 1;
    }

    private static List<CutPlane> DecodeActiveCutPlanes(byte[] source, int searchStart)
    {
        List<(int Offset, CutPlane Plane)> candidates = [];
        int searchEnd = Math.Min(source.Length, searchStart + 5_000_000);
        for (int offset = Math.Max(0, searchStart - 250_000); offset < searchEnd - 8; offset++)
        {
            bool isPie = source[offset] == (byte)'P'
                && source[offset + 1] == (byte)'i'
                && source[offset + 2] == (byte)'e';
            bool isSaw = source[offset] == (byte)'S'
                && source[offset + 1] == (byte)'a'
                && source[offset + 2] == (byte)'w';
            if (!isPie && !isSaw) continue;

            int end = offset + 3;
            while (end < searchEnd && source[end] is >= (byte)'0' and <= (byte)'9') end++;
            if (end == offset + 3 || end >= searchEnd || source[end++] != (byte)'-') continue;
            int itemStart = end;
            while (end < searchEnd && source[end] is >= (byte)'0' and <= (byte)'9') end++;
            if (end == itemStart || end - offset > 24) continue;

            string name = Encoding.ASCII.GetString(source, offset, end - offset);
            string planText = name[3..name.IndexOf('-')];
            if (!int.TryParse(planText, out int planNumber)) continue;

            CutPlane? decoded = null;
            for (int record = Math.Max(0, offset - 80); record <= offset - 48; record++)
            {
                double width = ReadDouble(source, record);
                double distance = ReadDouble(source, record + 8);
                Vertex normal = new(
                    ReadDouble(source, offset - 32),
                    ReadDouble(source, offset - 24),
                    ReadDouble(source, offset - 16));
                double magnitude = Math.Sqrt(Dot(normal, normal));
                if (width is < 1 or > 500
                    || !double.IsFinite(distance) || Math.Abs(distance) > 10_000_000
                    || Math.Abs(magnitude - 1) > 1e-5)
                {
                    continue;
                }

                decoded = new CutPlane(name, planNumber, width, distance, normal);
                break;
            }
            if (decoded is not null)
            {
                candidates.Add((offset, decoded));
                offset = end - 1;
            }
        }

        if (candidates.Count == 0)
        {
            throw new AdvFormatException("The active Pie/Saw cutting plan was not found.");
        }

        int activePlan = candidates[0].Plane.PlanNumber;
        List<CutPlane> result = candidates
            .Where(candidate => candidate.Plane.PlanNumber == activePlan)
            .TakeWhile(candidate => candidate.Offset - candidates[0].Offset < 100_000)
            .Select(candidate => candidate.Plane)
            .ToList();
        if (result.Count == 0)
        {
            throw new AdvFormatException("The active Pie/Saw cutting plan is empty.");
        }
        return result;
    }

    private static Mesh SliceMesh(Mesh source, CutPlane cut)
    {
        Mesh upper = ClipClosedMesh(source, cut.Normal, cut.Distance, keepLessOrEqual: true);
        Mesh slab = ClipClosedMesh(
            upper,
            cut.Normal,
            cut.Distance - cut.Width,
            keepLessOrEqual: false);
        if (slab.Vertices.Count < 4 || slab.Faces.Count < 4)
        {
            double minimum = source.Vertices.Min(vertex => Dot(cut.Normal, vertex));
            double maximum = source.Vertices.Max(vertex => Dot(cut.Normal, vertex));
            throw new AdvFormatException(
                $"Cut object {cut.Name} does not intersect the rough mesh "
                + $"(mesh range {minimum:G8}..{maximum:G8}, cut {cut.Distance - cut.Width:G8}..{cut.Distance:G8}).");
        }
        return slab;
    }

    private static Mesh ClipClosedMesh(Mesh source, Vertex normal, double offset, bool keepLessOrEqual)
    {
        var builder = new MeshBuilder();
        foreach ((int a, int b, int c) in source.Faces)
        {
            List<Vertex> polygon = [source.Vertices[a], source.Vertices[b], source.Vertices[c]];
            polygon = ClipPolygon(polygon, normal, offset, keepLessOrEqual);
            if (polygon.Count < 3) continue;

            int first = builder.AddVertex(polygon[0]);
            for (int index = 1; index + 1 < polygon.Count; index++)
            {
                builder.AddFace(first, builder.AddVertex(polygon[index]), builder.AddVertex(polygon[index + 1]));
            }
        }

        builder.CapOpenBoundaries();
        return new Mesh(builder.Vertices, builder.Faces, source.RepairedVertexCount, source.Warnings);
    }

    private static List<Vertex> ClipPolygon(
        List<Vertex> input,
        Vertex normal,
        double offset,
        bool keepLessOrEqual)
    {
        const double epsilon = 1e-8;
        List<Vertex> output = [];
        for (int index = 0; index < input.Count; index++)
        {
            Vertex current = input[index];
            Vertex next = input[(index + 1) % input.Count];
            double currentDistance = Dot(normal, current) - offset;
            double nextDistance = Dot(normal, next) - offset;
            bool currentInside = keepLessOrEqual
                ? currentDistance <= epsilon
                : currentDistance >= -epsilon;
            bool nextInside = keepLessOrEqual
                ? nextDistance <= epsilon
                : nextDistance >= -epsilon;

            if (currentInside) output.Add(current);
            if (currentInside == nextInside) continue;

            double ratio = currentDistance / (currentDistance - nextDistance);
            output.Add(new Vertex(
                current.X + (next.X - current.X) * ratio,
                current.Y + (next.Y - current.Y) * ratio,
                current.Z + (next.Z - current.Z) * ratio));
        }
        return output;
    }

    private static async Task WriteSawsIniAsync(
        string path,
        List<CutPlane> cuts,
        CancellationToken token)
    {
        StringBuilder contents = new();
        contents.AppendLine("[saws]");
        contents.AppendLine(cuts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (CutPlane cut in cuts)
        {
            token.ThrowIfCancellationRequested();
            contents.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{cut.Name} Width:{cut.Width:G17}"));
        }
        await WriteTextAtomicallyAsync(path, contents.ToString(), token);
    }

    private static Task WriteGalaxySymbolsAsync(
        string path,
        byte[] source,
        CancellationToken token)
    {
        List<GalaxySymbol> symbols = DecodeGalaxySymbols(source);
        string contents = string.Join(
            Environment.NewLine,
            symbols.Select(symbol => string.Create(
                CultureInfo.InvariantCulture,
                $"{symbol.Label}, {(float)symbol.Position.X:G7}, {(float)symbol.Position.Y:G7}, {(float)symbol.Position.Z:G7}")))
            + Environment.NewLine;
        return WriteTextAtomicallyAsync(path, contents, token);
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string contents,
        CancellationToken token)
    {
        await File.WriteAllTextAsync(path, contents, new UTF8Encoding(false), token);
    }

    private static List<GalaxySymbol> DecodeGalaxySymbols(byte[] source)
    {
        string[] labels = ["X", "T", "V", "(X)", "(T)", "(V)", "K"];
        List<GalaxyCandidate> candidates = [];
        int start = Math.Max(0, source.Length - 4_000_000);
        for (int offset = start; offset <= source.Length - 64; offset++)
        {
            uint code = ReadUInt32(source, offset);
            uint ordinal = ReadUInt32(source, offset + 4);
            uint variant = ReadUInt32(source, offset + 8);
            uint reserved = ReadUInt32(source, offset + 12);
            if (code > 20 || ordinal >= labels.Length) continue;

            int coordinateOffset;
            if (variant <= 1 && reserved == 0)
            {
                coordinateOffset = offset + 16;
            }
            else if (variant == 2 && reserved == 1)
            {
                coordinateOffset = offset + 40;
            }
            else
            {
                continue;
            }

            Vertex position = new(
                ReadDouble(source, coordinateOffset),
                ReadDouble(source, coordinateOffset + 8),
                ReadDouble(source, coordinateOffset + 16));
            if (!IsGalaxyPosition(position)) continue;
            candidates.Add(new GalaxyCandidate(offset, (int)code, (int)ordinal, position));
        }

        List<GalaxyCandidate>? required = SelectGalaxySequence(candidates, 6);
        if (required is null)
        {
            throw new AdvFormatException("The Galaxy symbol coordinate table was not found.");
        }

        HashSet<int> usedCodes = required.Select(candidate => candidate.Code).ToHashSet();
        int minimumOffset = required.Min(candidate => candidate.Offset);
        int maximumOffset = required.Max(candidate => candidate.Offset);
        GalaxyCandidate? optional = candidates
            .Where(candidate => candidate.Ordinal == 6
                && !usedCodes.Contains(candidate.Code)
                && candidate.Offset >= minimumOffset - 500_000
                && candidate.Offset <= maximumOffset + 500_000)
            .OrderBy(candidate => Math.Abs(candidate.Offset - maximumOffset))
            .FirstOrDefault();
        if (optional is not null) required.Add(optional);

        return required
            .OrderBy(candidate => candidate.Ordinal)
            .Select(candidate => new GalaxySymbol(labels[candidate.Ordinal], candidate.Position))
            .ToList();
    }

    private static List<GalaxyCandidate>? SelectGalaxySequence(
        List<GalaxyCandidate> candidates,
        int requiredCount)
    {
        List<GalaxyCandidate>[] byOrdinal = Enumerable.Range(0, requiredCount)
            .Select(ordinal => candidates.Where(candidate => candidate.Ordinal == ordinal).ToList())
            .ToArray();
        if (byOrdinal.Any(group => group.Count == 0)) return null;

        List<GalaxyCandidate>? best = null;
        int bestSpan = int.MaxValue;
        void Search(int ordinal, List<GalaxyCandidate> selected, HashSet<int> usedCodes)
        {
            if (ordinal == requiredCount)
            {
                int span = selected.Max(item => item.Offset) - selected.Min(item => item.Offset);
                if (span <= 2_000_000 && span < bestSpan)
                {
                    bestSpan = span;
                    best = [.. selected];
                }
                return;
            }
            foreach (GalaxyCandidate candidate in byOrdinal[ordinal])
            {
                if (!usedCodes.Add(candidate.Code)) continue;
                selected.Add(candidate);
                Search(ordinal + 1, selected, usedCodes);
                selected.RemoveAt(selected.Count - 1);
                usedCodes.Remove(candidate.Code);
            }
        }
        Search(0, [], []);
        return best;
    }

    private static bool IsGalaxyPosition(Vertex position)
    {
        if (!IsPlausibleCoordinate(position.X)
            || !IsPlausibleCoordinate(position.Y)
            || !IsPlausibleCoordinate(position.Z))
        {
            return false;
        }
        return new[] { position.X, position.Y, position.Z }.Count(value => Math.Abs(value) > 1) >= 2;
    }

    private static Mesh DecodeMesh(byte[] payload)
    {
        int faceCount = CountFaceRecords(payload);
        if (faceCount < 4 || (faceCount & 1) != 0)
        {
            throw new AdvFormatException("A valid triangular rough mesh was not found.");
        }

        int vertexCount = checked((faceCount + 4) / 2);
        int faceStart = checked(payload.Length - 4 - faceCount * 16);
        uint encodedFaceCount = ReadUInt32(payload, faceStart);
        if (!DecodeUnique(encodedFaceCount, (uint)(faceCount + 1), out uint decodedFaceCount)
            || decodedFaceCount != faceCount)
        {
            throw new AdvFormatException("The rough-mesh face table is inconsistent.");
        }

        List<(int A, int B, int C)> faces = new(faceCount);
        for (int index = 0; index < faceCount; index++)
        {
            int record = faceStart + 4 + index * 16;
            uint marker = ReadUInt32(payload, record);
            if (!FaceMarkers.Contains(marker))
            {
                throw new AdvFormatException($"Unsupported face marker at triangle {index + 1:N0}.");
            }

            int a = DecodeIndex(ReadUInt32(payload, record + 4), vertexCount, index);
            int b = DecodeIndex(ReadUInt32(payload, record + 8), vertexCount, index);
            int c = DecodeIndex(ReadUInt32(payload, record + 12), vertexCount, index);
            if (a == b || b == c || c == a)
            {
                throw new AdvFormatException($"Degenerate triangle at record {index + 1:N0}.");
            }
            faces.Add((a, b, c));
        }

        ValidateClosedTopology(vertexCount, faces);
        (List<Vertex> vertices, int repaired, List<string> warnings) =
            DecodeVertices(payload, faceStart, vertexCount, faces);

        return new Mesh(vertices, faces, repaired, warnings);
    }

    private static Mesh RepairDamagedMesh(byte[] payload)
    {
        int encodedFaceCount = CountFaceRecords(payload);
        if (encodedFaceCount < 100)
        {
            throw new AdvFormatException("A recoverable rough-mesh triangle table was not found.");
        }

        int faceStart = checked(payload.Length - 4 - encodedFaceCount * 16);
        List<(uint Marker, uint A, uint B, uint C)> records = new(encodedFaceCount);
        List<uint> plausibleIndices = [];
        for (int index = 0; index < encodedFaceCount; index++)
        {
            int offset = faceStart + 4 + index * 16;
            uint marker = ReadUInt32(payload, offset);
            uint mask = marker ^ 3u;
            uint a = ReadUInt32(payload, offset + 4) ^ mask;
            uint b = ReadUInt32(payload, offset + 8) ^ mask;
            uint c = ReadUInt32(payload, offset + 12) ^ mask;
            records.Add((marker, a, b, c));
            uint generousLimit = (uint)(encodedFaceCount / 2 + 1024);
            if (a < generousLimit) plausibleIndices.Add(a);
            if (b < generousLimit) plausibleIndices.Add(b);
            if (c < generousLimit) plausibleIndices.Add(c);
        }

        if (plausibleIndices.Count < encodedFaceCount * 2)
        {
            throw new AdvFormatException("Too few rough-mesh indices survived for safe repair.");
        }

        int vertexCount = checked((int)plausibleIndices.Max() + 1);
        int vertexStart = checked(faceStart - vertexCount * 24);
        if (vertexStart < 0)
        {
            throw new AdvFormatException("The recoverable rough-mesh vertex table is incomplete.");
        }

        Vertex?[] decodedVertices = new Vertex?[vertexCount];
        for (int index = 0; index < vertexCount; index++)
        {
            decodedVertices[index] = ReadVertex(payload, vertexStart + index * 24);
        }
        HashSet<int> repairedCoordinates = RepairInvalidCoordinates(decodedVertices);
        List<Vertex> vertices = decodedVertices.Select(vertex => vertex!.Value).ToList();

        List<((int A, int B, int C) Face, double LongestEdge)> candidates = [];
        foreach ((uint marker, uint a, uint b, uint c) in records)
        {
            if (!FaceMarkers.Contains(marker)
                || a >= vertexCount || b >= vertexCount || c >= vertexCount
                || a == b || b == c || c == a)
            {
                continue;
            }
            var face = ((int)a, (int)b, (int)c);
            double longest = Math.Max(
                Distance(vertices[face.Item1], vertices[face.Item2]),
                Math.Max(
                    Distance(vertices[face.Item2], vertices[face.Item3]),
                    Distance(vertices[face.Item3], vertices[face.Item1])));
            if (double.IsFinite(longest)) candidates.Add((face, longest));
        }
        if (candidates.Count < vertexCount)
        {
            throw new AdvFormatException("Too few rough-mesh triangles survived for safe repair.");
        }

        double medianEdge = candidates.Select(candidate => candidate.LongestEdge)
            .OrderBy(value => value)
            .ElementAt(candidates.Count / 2);
        double maximumTrustedEdge = Math.Max(50, medianEdge * 1.5);

        DMesh3 repairMesh = new(false, false, false, false);
        foreach (Vertex vertex in vertices)
        {
            repairMesh.AppendVertex(new Vector3d(vertex.X, vertex.Y, vertex.Z));
        }
        int retained = 0;
        foreach (((int a, int b, int c), double longestEdge) in candidates)
        {
            if (longestEdge > maximumTrustedEdge) continue;
            if (repairMesh.AppendTriangle(a, b, c, -1) >= 0) retained++;
        }
        if (retained < vertexCount)
        {
            throw new AdvFormatException("The rough mesh is too damaged to reconstruct.");
        }

        MeshAutoRepair repair = new(repairMesh)
        {
            RepairTolerance = Math.Max(0.001, medianEdge * 0.01),
            MinEdgeLengthTol = Math.Max(0.0001, medianEdge * 0.001),
        };
        if (!repair.Apply() || !repairMesh.CachedIsClosed)
        {
            throw new AdvFormatException("The rough-mesh topology could not be closed safely.");
        }

        MeshConnectedComponents components = new(repairMesh);
        components.FindConnectedT();
        if (components.Count > 1)
        {
            int largest = components.LargestByCount;
            HashSet<int> keep = components[largest].Indices.ToHashSet();
            MeshEditor editor = new(repairMesh);
            editor.RemoveTriangles(
                repairMesh.TriangleIndices().Where(triangleId => !keep.Contains(triangleId)).ToArray(),
                true);
            editor.RemoveUnusedVertices();
        }

        Dictionary<int, int> compactIndices = [];
        List<Vertex> repairedVertices = [];
        foreach (int vertexId in repairMesh.VertexIndices())
        {
            Vector3d vertex = repairMesh.GetVertex(vertexId);
            compactIndices[vertexId] = repairedVertices.Count;
            repairedVertices.Add(new Vertex(vertex.x, vertex.y, vertex.z));
        }
        List<(int A, int B, int C)> repairedFaces = [];
        foreach (int triangleId in repairMesh.TriangleIndices())
        {
            Index3i triangle = repairMesh.GetTriangle(triangleId);
            repairedFaces.Add((
                compactIndices[triangle.a],
                compactIndices[triangle.b],
                compactIndices[triangle.c]));
        }
        ValidateClosedTopology(repairedVertices.Count, repairedFaces);
        List<string> warnings =
        [
            $"Recovered damaged topology from {retained:N0} trustworthy source triangles.",
        ];
        if (repairedCoordinates.Count > 0)
        {
            warnings.Add($"Repaired coordinates in {repairedCoordinates.Count:N0} vertices.");
        }
        return new Mesh(
            repairedVertices,
            repairedFaces,
            repairedCoordinates.Count,
            warnings);
    }

    private static double Distance(Vertex a, Vertex b)
    {
        double x = a.X - b.X;
        double y = a.Y - b.Y;
        double z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    private static int CountFaceRecords(byte[] payload)
    {
        int count = 0;
        for (int offset = payload.Length - 16; offset >= 0; offset -= 16)
        {
            if (!FaceMarkers.Contains(ReadUInt32(payload, offset)))
            {
                break;
            }
            count++;
        }
        return count;
    }

    private static int DecodeIndex(uint encoded, int vertexCount, int faceIndex)
    {
        if (!DecodeUnique(encoded, (uint)vertexCount, out uint decoded))
        {
            throw new AdvFormatException(
                $"Triangle {faceIndex + 1:N0} contains an unsupported encoded vertex index.");
        }
        return checked((int)decoded);
    }

    private static bool DecodeUnique(uint encoded, uint exclusiveMaximum, out uint decoded)
    {
        uint candidate = 0;
        bool found = false;
        foreach (uint mask in AdvisorMasks)
        {
            uint value = encoded ^ mask;
            if (value >= exclusiveMaximum)
            {
                continue;
            }

            if (found && candidate != value)
            {
                decoded = 0;
                return false;
            }
            candidate = value;
            found = true;
        }
        decoded = candidate;
        return found;
    }

    private static (List<Vertex>, int, List<string>) DecodeVertices(
        byte[] payload,
        int faceStart,
        int vertexCount,
        List<(int A, int B, int C)> faces)
    {
        long expectedBytes = (long)vertexCount * 24;
        int estimatedStart = checked(faceStart - (int)expectedBytes);
        int vertexStart = FindBestVertexStart(payload, estimatedStart, faceStart, vertexCount);
        int missingBytes = checked((int)(expectedBytes - (faceStart - vertexStart)));
        if (missingBytes < 0 || missingBytes > 128)
        {
            throw new AdvFormatException("The rough-mesh vertex table has an unsupported layout.");
        }

        Vertex?[] decoded = new Vertex?[vertexCount];
        HashSet<int> repairedVertices = [];
        List<string> warnings = [];
        int repairWindowStart = -1;

        if (missingBytes == 0)
        {
            for (int index = 0; index < vertexCount; index++)
            {
                decoded[index] = ReadVertex(payload, vertexStart + index * 24);
            }
        }
        else
        {
            int split = -1;
            for (int index = 0; index < vertexCount; index++)
            {
                Vertex vertex = ReadVertex(payload, vertexStart + index * 24);
                if (!IsPlausible(vertex))
                {
                    split = index;
                    break;
                }
                decoded[index] = vertex;
            }

            // Both damaged supplied layouts replace four vertices with a short
            // proprietary marker. Suffix coordinates remain end-aligned.
            const int missingVertices = 4;
            if (split <= 0 || split + missingVertices >= vertexCount
                || missingVertices * 24 - missingBytes is < 0 or > 64)
            {
                throw new AdvFormatException("The damaged vertex marker could not be repaired.");
            }

            for (int index = split + missingVertices; index < vertexCount; index++)
            {
                int offset = checked(faceStart - (vertexCount - index) * 24);
                decoded[index] = ReadVertex(payload, offset);
            }

            InterpolateMissingVertices(decoded, split, missingVertices);
            repairWindowStart = split;
            for (int index = split; index < split + missingVertices; index++)
            {
                repairedVertices.Add(index);
            }
            warnings.Add($"Reconstructed {missingVertices} vertices from adjacent surface points.");
        }

        repairedVertices.UnionWith(RepairInvalidCoordinates(decoded));
        if (repairWindowStart >= 0)
        {
            repairedVertices.UnionWith(RepairTopologyOutliers(
                decoded,
                faces,
                repairWindowStart,
                Math.Min(vertexCount, repairWindowStart + 1000)));
        }

        if (repairedVertices.Count > 0 && warnings.Count == 0)
        {
            warnings.Add($"Repaired {repairedVertices.Count} vertices containing damaged coordinates.");
        }
        else if (repairedVertices.Count > 4 && warnings.Count > 0)
        {
            warnings.Add($"Repaired coordinates in {repairedVertices.Count - 4} additional vertices.");
        }

        return (decoded.Select(vertex => vertex!.Value).ToList(), repairedVertices.Count, warnings);
    }

    private static int FindBestVertexStart(byte[] payload, int estimate, int faceStart, int vertexCount)
    {
        int sampleCount = Math.Min(100, vertexCount);
        int minimum = Math.Max(0, estimate - 16);
        int maximum = Math.Min(faceStart - sampleCount * 24, estimate + 128);
        int bestOffset = -1;
        int bestScore = -1;
        int bestDistance = int.MaxValue;

        for (int candidate = minimum; candidate <= maximum; candidate++)
        {
            int score = 0;
            for (int index = 0; index < sampleCount; index++)
            {
                if (IsPlausible(ReadVertex(payload, candidate + index * 24)))
                {
                    score++;
                }
            }
            int distance = Math.Abs(candidate - estimate);
            if (score > bestScore || (score == bestScore && distance < bestDistance))
            {
                bestScore = score;
                bestDistance = distance;
                bestOffset = candidate;
            }
        }

        if (bestOffset < 0 || bestScore < sampleCount * 0.95)
        {
            throw new AdvFormatException("The rough-mesh vertex table could not be located.");
        }
        return bestOffset;
    }

    private static HashSet<int> RepairInvalidCoordinates(Vertex?[] vertices)
    {
        HashSet<int> repaired = [];
        for (int index = 0; index < vertices.Length; index++)
        {
            Vertex current = vertices[index]!.Value;
            bool invalidX = !IsPlausibleCoordinate(current.X);
            bool invalidY = !IsPlausibleCoordinate(current.Y);
            bool invalidZ = !IsPlausibleCoordinate(current.Z);
            if (!invalidX && !invalidY && !invalidZ)
            {
                continue;
            }

            double x = invalidX ? InterpolateCoordinate(vertices, index, v => v.X) : current.X;
            double y = invalidY ? InterpolateCoordinate(vertices, index, v => v.Y) : current.Y;
            double z = invalidZ ? InterpolateCoordinate(vertices, index, v => v.Z) : current.Z;
            vertices[index] = new Vertex(x, y, z);
            repaired.Add(index);
        }
        return repaired;
    }

    private static HashSet<int> RepairTopologyOutliers(
        Vertex?[] vertices,
        List<(int A, int B, int C)> faces,
        int start,
        int end)
    {
        HashSet<int>[] neighbours = Enumerable.Range(0, vertices.Length)
            .Select(_ => new HashSet<int>())
            .ToArray();
        foreach ((int a, int b, int c) in faces)
        {
            neighbours[a].UnionWith([b, c]);
            neighbours[b].UnionWith([a, c]);
            neighbours[c].UnionWith([a, b]);
        }

        HashSet<int> repaired = [];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            List<(int Index, int Axis, double Value)> changes = [];
            for (int index = start; index < end; index++)
            {
                Vertex current = vertices[index]!.Value;
                for (int axis = 0; axis < 3; axis++)
                {
                    List<double> values = neighbours[index]
                        .Select(neighbour => Coordinate(vertices[neighbour]!.Value, axis))
                        .Where(IsPlausibleCoordinate)
                        .Order()
                        .ToList();
                    if (values.Count < 3) continue;

                    double median = Median(values);
                    List<double> deviations = values
                        .Select(value => Math.Abs(value - median))
                        .Order()
                        .ToList();
                    double mad = Median(deviations);
                    double value = Coordinate(current, axis);
                    if (!IsPlausibleCoordinate(value)
                        || Math.Abs(value - median) > Math.Max(30.0, 3.0 * Math.Max(mad, 1.0)))
                    {
                        changes.Add((index, axis, median));
                    }
                }
            }

            if (changes.Count == 0) break;
            foreach ((int index, int axis, double value) in changes)
            {
                Vertex current = vertices[index]!.Value;
                vertices[index] = axis switch
                {
                    0 => current with { X = value },
                    1 => current with { Y = value },
                    _ => current with { Z = value },
                };
                repaired.Add(index);
            }
        }
        return repaired;
    }

    private static double Coordinate(Vertex vertex, int axis) => axis switch
    {
        0 => vertex.X,
        1 => vertex.Y,
        _ => vertex.Z,
    };

    private static double Median(List<double> sorted)
    {
        int middle = sorted.Count / 2;
        return (sorted.Count & 1) == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double InterpolateCoordinate(
        Vertex?[] vertices,
        int index,
        Func<Vertex, double> selector)
    {
        int left = index - 1;
        while (left >= 0 && (!vertices[left].HasValue
            || !IsPlausibleCoordinate(selector(vertices[left]!.Value))))
        {
            left--;
        }

        int right = index + 1;
        while (right < vertices.Length && (!vertices[right].HasValue
            || !IsPlausibleCoordinate(selector(vertices[right]!.Value))))
        {
            right++;
        }

        if (left < 0 && right >= vertices.Length)
        {
            throw new AdvFormatException("A damaged coordinate could not be reconstructed.");
        }
        if (left < 0) return selector(vertices[right]!.Value);
        if (right >= vertices.Length) return selector(vertices[left]!.Value);

        double ratio = (double)(index - left) / (right - left);
        return selector(vertices[left]!.Value)
            + (selector(vertices[right]!.Value) - selector(vertices[left]!.Value)) * ratio;
    }

    private static void InterpolateMissingVertices(Vertex?[] vertices, int start, int count)
    {
        Vertex left = vertices[start - 1]!.Value;
        Vertex right = vertices[start + count]!.Value;
        for (int offset = 0; offset < count; offset++)
        {
            double ratio = (double)(offset + 1) / (count + 1);
            vertices[start + offset] = new Vertex(
                left.X + (right.X - left.X) * ratio,
                left.Y + (right.Y - left.Y) * ratio,
                left.Z + (right.Z - left.Z) * ratio);
        }
    }

    private static void ValidateClosedTopology(int vertexCount, List<(int A, int B, int C)> faces)
    {
        if (faces.Count != vertexCount * 2 - 4)
        {
            throw new AdvFormatException(
                $"The rough mesh does not satisfy the expected closed-surface topology "
                + $"({vertexCount:N0} vertices, {faces.Count:N0} triangles).");
        }

        Dictionary<ulong, int> edges = new(faces.Count * 3 / 2);
        foreach ((int a, int b, int c) in faces)
        {
            AddEdge(edges, a, b);
            AddEdge(edges, b, c);
            AddEdge(edges, c, a);
        }
        if (edges.Values.Any(count => count != 2))
        {
            throw new AdvFormatException("The decoded rough mesh is not watertight.");
        }
    }

    private static void AddEdge(Dictionary<ulong, int> edges, int a, int b)
    {
        uint low = (uint)Math.Min(a, b);
        uint high = (uint)Math.Max(a, b);
        ulong key = ((ulong)high << 32) | low;
        edges.TryGetValue(key, out int count);
        edges[key] = count + 1;
    }

    private static bool IsPlausible(Vertex vertex) =>
        IsPlausibleCoordinate(vertex.X)
        && IsPlausibleCoordinate(vertex.Y)
        && IsPlausibleCoordinate(vertex.Z);

    private static bool IsPlausibleCoordinate(double value) =>
        double.IsFinite(value)
        && Math.Abs(value) < 10_000_000
        && (value == 0 || Math.Abs(value) > 1e-20);

    private static Vertex ReadVertex(byte[] data, int offset)
    {
        EnsureRange(data, offset, 24);
        return new Vertex(
            BitConverter.Int64BitsToDouble((long)ReadUInt64(data, offset)),
            BitConverter.Int64BitsToDouble((long)ReadUInt64(data, offset + 8)),
            BitConverter.Int64BitsToDouble((long)ReadUInt64(data, offset + 16)));
    }

    private static async Task WriteObjAtomicallyAsync(
        string path,
        Mesh mesh,
        string groupName,
        CancellationToken token)
    {
        await WriteObjAsync(path, mesh, groupName, token);
    }

    private static async Task WriteObjAsync(
        string path,
        Mesh mesh,
        string groupName,
        CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync("# ADV2OBJ mesh");
        await writer.WriteLineAsync("# Source format: Sarine Advisor ADV");
        await writer.WriteLineAsync($"g {groupName}");
        foreach (Vertex vertex in mesh.Vertices)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"v {vertex.X:G17} {vertex.Y:G17} {vertex.Z:G17}"));
        }
        foreach ((int a, int b, int c) in mesh.Faces)
        {
            token.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"f {a + 1} {b + 1} {c + 1}");
        }
    }

    private static int FindBytes(byte[] data, byte[] pattern, int start, int end)
    {
        int maximum = Math.Min(end, data.Length) - pattern.Length;
        for (int offset = Math.Max(0, start); offset <= maximum; offset++)
        {
            if (data.AsSpan(offset, pattern.Length).SequenceEqual(pattern))
            {
                return offset;
            }
        }
        return -1;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        EnsureRange(data, offset, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        EnsureRange(data, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    private static ulong ReadUInt64(byte[] data, int offset)
    {
        EnsureRange(data, offset, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8));
    }

    private static double ReadDouble(byte[] data, int offset) =>
        BitConverter.Int64BitsToDouble((long)ReadUInt64(data, offset));

    private static double Dot(Vertex left, Vertex right) =>
        left.X * right.X + left.Y * right.Y + left.Z * right.Z;

    private static void EnsureRange(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new AdvFormatException("The ADV file is truncated or contains invalid offsets.");
        }
    }

    private readonly record struct Vertex(double X, double Y, double Z);

    private sealed record CutPlane(
        string Name,
        int PlanNumber,
        double Width,
        double Distance,
        Vertex Normal);

    private sealed record GalaxyCandidate(int Offset, int Code, int Ordinal, Vertex Position);

    private sealed record GalaxySymbol(string Label, Vertex Position);

    private sealed class MeshBuilder
    {
        private const double Quantization = 1_000_000.0;
        private readonly Dictionary<(long X, long Y, long Z), int> _vertexLookup = [];

        public List<Vertex> Vertices { get; } = [];
        public List<(int A, int B, int C)> Faces { get; } = [];

        public int AddVertex(Vertex vertex)
        {
            var key = (
                checked((long)Math.Round(vertex.X * Quantization)),
                checked((long)Math.Round(vertex.Y * Quantization)),
                checked((long)Math.Round(vertex.Z * Quantization)));
            if (_vertexLookup.TryGetValue(key, out int existing)) return existing;
            int index = Vertices.Count;
            Vertices.Add(vertex);
            _vertexLookup.Add(key, index);
            return index;
        }

        public void AddFace(int a, int b, int c)
        {
            if (a != b && b != c && c != a) Faces.Add((a, b, c));
        }

        public void CapOpenBoundaries()
        {
            Dictionary<ulong, (int Count, int A, int B)> edges = [];
            foreach ((int a, int b, int c) in Faces)
            {
                CountEdge(edges, a, b);
                CountEdge(edges, b, c);
                CountEdge(edges, c, a);
            }

            List<(int A, int B)> boundary = edges.Values
                .Where(edge => edge.Count == 1)
                .Select(edge => (edge.A, edge.B))
                .ToList();
            Dictionary<int, Queue<int>> nextByStart = [];
            foreach ((int a, int b) in boundary)
            {
                if (!nextByStart.TryGetValue(a, out Queue<int>? queue))
                {
                    queue = new Queue<int>();
                    nextByStart[a] = queue;
                }
                queue.Enqueue(b);
            }

            HashSet<ulong> used = [];
            foreach ((int start, int firstNext) in boundary)
            {
                ulong firstKey = DirectedKey(start, firstNext);
                if (used.Contains(firstKey)) continue;

                List<int> loop = [start];
                int current = start;
                int next = firstNext;
                while (true)
                {
                    used.Add(DirectedKey(current, next));
                    current = next;
                    if (current == start) break;
                    loop.Add(current);
                    if (!nextByStart.TryGetValue(current, out Queue<int>? choices)) break;
                    int chosen = -1;
                    foreach (int choice in choices)
                    {
                        if (!used.Contains(DirectedKey(current, choice)))
                        {
                            chosen = choice;
                            break;
                        }
                    }
                    if (chosen < 0) break;
                    next = chosen;
                    if (loop.Count > boundary.Count + 1) break;
                }
                if (current != start || loop.Count < 3) continue;

                Vertex center = new(
                    loop.Average(index => Vertices[index].X),
                    loop.Average(index => Vertices[index].Y),
                    loop.Average(index => Vertices[index].Z));
                int centerIndex = AddVertex(center);
                for (int index = 0; index < loop.Count; index++)
                {
                    int a = loop[index];
                    int b = loop[(index + 1) % loop.Count];
                    AddFace(b, a, centerIndex);
                }
            }
        }

        private static void CountEdge(
            Dictionary<ulong, (int Count, int A, int B)> edges,
            int a,
            int b)
        {
            uint low = (uint)Math.Min(a, b);
            uint high = (uint)Math.Max(a, b);
            ulong key = ((ulong)high << 32) | low;
            if (edges.TryGetValue(key, out var edge))
            {
                edges[key] = (edge.Count + 1, edge.A, edge.B);
            }
            else
            {
                edges[key] = (1, a, b);
            }
        }

        private static ulong DirectedKey(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
    }

    private sealed class ThrottledReadStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(1, count));
        public override int Read(Span<byte> buffer) => inner.Read(buffer[..Math.Min(1, buffer.Length)]);
        public override int ReadByte() => inner.ReadByte();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }

    private sealed record Mesh(
        List<Vertex> Vertices,
        List<(int A, int B, int C)> Faces,
        int RepairedVertexCount,
        List<string> Warnings);
}
