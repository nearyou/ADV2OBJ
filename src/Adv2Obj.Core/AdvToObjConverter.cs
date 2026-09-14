using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

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
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        byte[] source = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        ValidateAdv(source);

        byte[] payload = DecompressRoughPayload(source);
        Mesh mesh = DecodeMesh(payload);

        string? directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = outputPath + ".tmp";
        try
        {
            await WriteObjAsync(temporaryPath, mesh, cancellationToken);
            File.Move(temporaryPath, outputPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new ConversionResult(
            inputPath,
            outputPath,
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

    private static byte[] DecompressRoughPayload(byte[] source)
    {
        int metadataOffset = checked((int)ReadUInt32(source, 0x34));
        int searchEnd = Math.Min(source.Length, metadataOffset + 1024);
        int localHeader = FindBytes(source, ZipSignature, metadataOffset, searchEnd);

        int compressedSize;
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
            int nameLength = ReadUInt16(source, methodOffset + 18);
            int extraLength = ReadUInt16(source, methodOffset + 20);
            dataStart = checked(methodOffset + 22 + nameLength + extraLength);
        }

        EnsureRange(source, dataStart, compressedSize);
        try
        {
            using var compressed = new MemoryStream(source, dataStart, compressedSize, false);
            using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
            using var result = new MemoryStream();
            deflate.CopyTo(result);
            return result.ToArray();
        }
        catch (InvalidDataException exception)
        {
            throw new AdvFormatException(
                "The embedded rough-mesh stream is damaged and cannot be decompressed.",
                exception);
        }
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
            throw new AdvFormatException("The rough mesh does not satisfy the expected closed-surface topology.");
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

    private static async Task WriteObjAsync(string path, Mesh mesh, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false));
        await writer.WriteLineAsync("# ADV2OBJ rough mesh");
        await writer.WriteLineAsync("# Source format: Sarine Advisor ADV");
        await writer.WriteLineAsync("g Rough");
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

    private static void EnsureRange(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new AdvFormatException("The ADV file is truncated or contains invalid offsets.");
        }
    }

    private readonly record struct Vertex(double X, double Y, double Z);

    private sealed record Mesh(
        List<Vertex> Vertices,
        List<(int A, int B, int C)> Faces,
        int RepairedVertexCount,
        List<string> Warnings);
}
