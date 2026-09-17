using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using g3;
using gs;

namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static readonly byte[] AdvSignature =
        [0xCA, 0x9B, 0x28, 0xC7, 0xD7, 0xEC, 0x9B, 0x45, 0xAA, 0xBF, 0xE4, 0x42, 0x3F, 0xEF, 0x0E, 0xFF];

    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] ZippedDataName = Encoding.ASCII.GetBytes("ZippedData");
    private static readonly byte[] GalaxyTableSignature =
        [0xF3, 0x6B, 0xED, 0x43, 0xB3, 0x32, 0xFA, 0x60, 0x7E, 0xE3, 0x55, 0x1D];

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

    public async Task<ConversionAndCleanupResult> ConvertAndRemoveInputAsync(
        string inputPath,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        FileInfo original = new(inputPath);
        long originalLength = original.Length;
        DateTime originalLastWrite = original.LastWriteTimeUtc;
        ConversionResult conversion = await ConvertAsync(inputPath, outputRoot, cancellationToken);

        try
        {
            FileInfo current = new(inputPath);
            if (current.Exists && (current.Length != originalLength
                || current.LastWriteTimeUtc != originalLastWrite))
            {
                return new ConversionAndCleanupResult(
                    conversion, false, "The input ADV changed during conversion.");
            }
            File.Delete(inputPath);
            return new ConversionAndCleanupResult(conversion, true, null);
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new ConversionAndCleanupResult(conversion, false, error.Message);
        }
    }

    public async Task<ConversionResult> ConvertAsync(
        string inputPath,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        byte[] source = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        ValidateAdv(source);

        string stem = Path.GetFileNameWithoutExtension(inputPath);
        Directory.CreateDirectory(outputRoot);
        byte[] payload = [];
        int roughRecordEnd = checked((int)ReadUInt32(source, 0x34));
        bool alternateRawMesh = false;
        Mesh? recoveredFromInflate = null;
        try
        {
            (payload, roughRecordEnd, alternateRawMesh) = DecompressRoughPayload(source);
        }
        catch (AdvFormatException primaryError)
        {
            recoveredFromInflate = FindAlternateClosedMesh(source, roughRecordEnd, cancellationToken)
                ?? throw new AdvFormatException(
                    $"{primaryError.Message} No complete alternate mesh was found.");
            recoveredFromInflate.Warnings.Add(
                "Primary rough mesh cannot be decompressed; recovered a closed alternate mesh from the ADV. Verify its shape in MeshLab.");
        }
        Mesh mesh;
        if (recoveredFromInflate is not null)
        {
            mesh = recoveredFromInflate;
        }
        else try
        {
            mesh = DecodeMesh(payload);
        }
        catch (AdvFormatException)
        {
            Mesh? radialRecovery = TryRecoverPointSurface(payload, cancellationToken);
            Mesh? completeAlternate = radialRecovery ?? (CountFaceRecords(payload) < 100
                ? FindAlternateClosedMesh(source, roughRecordEnd, cancellationToken)
                : null);
            if (completeAlternate is not null)
            {
                mesh = completeAlternate;
                if (radialRecovery is null) mesh.Warnings.Add(
                    "Primary rough mesh is damaged; recovered a closed alternate mesh from the ADV. Verify its shape in MeshLab.");
            }
            else
            {
                try
                {
                    mesh = RepairDamagedMesh(payload);
                }
                catch (AdvFormatException primaryError)
                {
                    mesh = FindAlternateClosedMesh(source, roughRecordEnd, cancellationToken)
                        ?? throw new AdvFormatException(
                            $"{primaryError.Message} No complete alternate mesh was found.");
                    mesh.Warnings.Add(
                        "Primary rough mesh is damaged; recovered a closed alternate mesh from the ADV. Verify its shape in MeshLab.");
                }
            }
        }
        if (alternateRawMesh)
        {
            mesh.Warnings.Add(
                "Primary uncompressed rough mesh is damaged; recovered a later closed mesh from the ADV. Verify its shape in MeshLab.");
        }
        List<CutPlane> cuts = DecodeActiveCutPlanes(source, roughRecordEnd);
        List<CutGroup> groups = DecodeCutGroups(source, cuts);
        if (groups.Count > 0)
            cuts = groups.SelectMany(group => group.Cuts).Select(item => item.Plane).ToList();
        if (cuts.Count == 0)
        {
            mesh.Warnings.Add("No Pie/Saw cutting plan is present; exported the rough mesh only.");
        }
        Mesh cuttingMesh = mesh;

        string outputDirectory = Path.Combine(outputRoot, stem);
        // Decode every companion record before touching the destination.
        List<GalaxySymbol> symbols = DecodeGalaxySymbols(source);
        int repairedSymbols = symbols.Count(symbol => symbol.Repaired);
        if (repairedSymbols > 0)
        {
            mesh.Warnings.Add(
                $"Recovered {repairedSymbols:N0} Galaxy symbol {(repairedSymbols == 1 ? "record" : "records")} from shifted bytes; reduced precision requires visual review.");
        }
        if (symbols.Count == 5 && HasInactiveGalaxySlot(source))
        {
            mesh.Warnings.Add("One inactive Galaxy symbol slot was omitted from the CSV.");
        }
        string stagingDirectory = Path.Combine(outputRoot, ".adv2obj-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        try
        {

        await WriteObjAtomicallyAsync(
            Path.Combine(stagingDirectory, $"{stem}_Rough.obj"),
            mesh,
            "Rough",
            cancellationToken);

        foreach (CutPlane cut in cuts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Mesh slice = SliceMesh(cuttingMesh, cut, cuts, groups);
            await WriteObjAtomicallyAsync(
                Path.Combine(stagingDirectory, $"{stem}_{cut.Name}.obj"),
                slice,
                cut.Name,
                cancellationToken);
        }

        await WriteSawsIniAsync(
            Path.Combine(stagingDirectory, $"{stem}_SawsMD.ini"),
            cuts,
            cancellationToken);
        await WriteGalaxySymbolsAsync(
            Path.Combine(stagingDirectory, $"{stem}_GalaxySymbols.csv"),
            symbols,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        PublishOutput(stagingDirectory, outputDirectory, stem);
        List<string> warnings = [.. mesh.Warnings];
        if (cuts.Count > 0)
        {
            warnings.Add("Pie/Saw meshes are reconstructed from cut planes and require visual review.");
        }
        return new ConversionResult(
            inputPath,
            outputDirectory,
            cuts.Count + 1,
            mesh.Vertices.Count,
            mesh.Faces.Count,
            mesh.RepairedVertexCount,
            warnings);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory)) RetryFileOperation(() => Directory.Delete(stagingDirectory, true));
        }
    }

    private static void ValidateAdv(ReadOnlySpan<byte> data)
    {
        if (data.Length < 64 || !data[..AdvSignature.Length].SequenceEqual(AdvSignature))
        {
            throw new AdvFormatException("The file is not a supported Sarine Advisor ADV file.");
        }
    }

    private static void PublishOutput(string staging, string destination, string stem)
    {
        string root = Path.GetDirectoryName(destination)!;
        using FileStream outputLock = new(Path.Combine(root, $".adv2obj-{stem}.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (!Directory.Exists(destination))
        {
            RetryFileOperation(() => Directory.Move(staging, destination));
            return;
        }

        string backup = Path.Combine(root, ".adv2obj-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        List<string> published = [];
        List<string> previous = [];
        bool mayRemoveBackup = false;
        try
        {
            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(stem)
                + "_(Rough\\.obj|(?:Pie|Saw)\\d+-\\d+\\.obj|GalaxySymbols\\.csv|SawsMD\\.ini)$";
            foreach (string path in Directory.EnumerateFiles(destination))
            {
                string name = Path.GetFileName(path);
                if (!System.Text.RegularExpressions.Regex.IsMatch(name, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                RetryFileOperation(() => File.Move(path, Path.Combine(backup, name)));
                previous.Add(name);
            }
            foreach (string path in Directory.EnumerateFiles(staging))
            {
                string name = Path.GetFileName(path);
                RetryFileOperation(() => File.Move(path, Path.Combine(destination, name)));
                published.Add(name);
            }
            mayRemoveBackup = true;
        }
        catch (Exception publishError)
        {
            try
            {
                foreach (string name in published) File.Delete(Path.Combine(destination, name));
                foreach (string name in previous) File.Move(Path.Combine(backup, name), Path.Combine(destination, name));
                mayRemoveBackup = true;
            }
            catch (Exception rollbackError)
            {
                throw new IOException($"Output could not be restored. Previous files are retained at {backup}.",
                    new AggregateException(publishError, rollbackError));
            }
            throw;
        }
        finally
        {
            if (mayRemoveBackup) RetryFileOperation(() => Directory.Delete(backup, true));
        }
    }

    private static void RetryFileOperation(Action operation)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (IOException error) when (attempt < 5 && (error.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static (byte[] Payload, int RecordEnd, bool AlternateRawMesh) DecompressRoughPayload(byte[] source)
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
                return FindRawMeshPayload(source, metadataOffset)
                    ?? throw new AdvFormatException(
                        "No complete embedded ZIP or uncompressed rough mesh was found.");
            }

            int methodOffset = nameOffset - 22;
            EnsureRange(source, methodOffset, 22);
            ushort method = ReadUInt16(source, methodOffset);
            // Advisor 8.1 can lose the first part of this local header. In
            // observed files the method bytes become zero while the member is
            // still DEFLATE (compressed size is smaller than declared size).
            // Verify by inflating the bytes below rather than trusting them.
            if (method != 8 && method != 0)
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
            return (payload, checked(dataStart + compressedSize), false);
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
                        return (payload, checked(dataStart + compressedSize), false);
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
            return (bestCandidate, checked(dataStart + compressedSize), false);
        }

        throw new AdvFormatException("The embedded rough-mesh stream could not be decompressed.");
    }

    private static (byte[] Payload, int RecordEnd, bool AlternateRawMesh)? FindRawMeshPayload(
        byte[] source, int metadataOffset)
    {
        // Some Advisor 8.1 files store the same indexed mesh record directly,
        // without a ZIP member. Locate its closed triangle table by its count,
        // then let DecodeMesh validate every index and the complete topology.
        for (int offset = metadataOffset + 4; offset <= source.Length - 16; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, 4)) != 3)
            {
                continue;
            }

            int end = offset;
            while (end <= source.Length - 16
                   && BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(end, 4)) == 3)
            {
                end += 16;
            }
            int faceCount = (end - offset) / 16;
            if (faceCount >= 100
                && (faceCount & 1) == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset - 4, 4)) == faceCount)
            {
                byte[] candidate = source.AsSpan(metadataOffset, end - metadataOffset).ToArray();
                try
                {
                    _ = DecodeMesh(candidate);
                    bool alternate = offset - metadataOffset > 2_000_000;
                    // A later mesh can be intact after the primary record was
                    // damaged. The active cut plan still follows the primary
                    // record, so scan from the metadata area in that case.
                    return (candidate, alternate ? metadataOffset : end, alternate);
                }
                catch (AdvFormatException)
                {
                    // A face-count lookalike is not enough to prove a mesh.
                }
            }
            offset = end - 1;
        }
        return null;
    }

    private static Mesh? FindAlternateClosedMesh(
        byte[] source, int searchStart, CancellationToken cancellationToken)
    {
        Mesh? largest = null;
        int offset = searchStart;
        while ((offset = FindBytes(source, ZipSignature, offset, source.Length)) >= 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > source.Length - 30) break;
            int nameLength = ReadUInt16(source, offset + 26);
            int extraLength = ReadUInt16(source, offset + 28);
            long start = (long)offset + 30 + nameLength + extraLength;
            uint compressedSize = ReadUInt32(source, offset + 18);
            if (ReadUInt16(source, offset + 8) == 8
                && nameLength == ZippedDataName.Length
                && offset + 30 + nameLength <= source.Length
                && source.AsSpan(offset + 30, nameLength).SequenceEqual(ZippedDataName)
                && compressedSize is > 1000 and < 16_000_000
                && start + compressedSize <= source.Length)
            {
                byte[]? payload = TryInflate(source.AsSpan((int)start, (int)compressedSize).ToArray());
                if (payload is not null)
                {
                    try
                    {
                        Mesh candidate = DecodeMesh(payload);
                        if (largest is null || candidate.Vertices.Count > largest.Vertices.Count)
                        {
                            largest = candidate;
                        }
                    }
                    catch (AdvFormatException)
                    {
                        // Other embedded records are not complete rough meshes.
                    }
                }
            }
            offset++;
        }
        return largest;
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
                    || Math.Abs(magnitude - 1) > 0.02)
                {
                    continue;
                }

                // Keep the plane equation exactly as stored. Normalizing only
                // the normal, without scaling its distance and kerf bounds,
                // moves the cut. Clipping does not require a unit normal.
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
            if (!ContainsNamedCut(source))
            {
                return [];
            }
            throw new AdvFormatException("The active Pie/Saw cutting plan was not found.");
        }

        int activePlan = candidates[0].Plane.PlanNumber;
        List<CutPlane> result = candidates
            .Where(candidate => candidate.Plane.PlanNumber == activePlan)
            .TakeWhile(candidate => candidate.Offset - candidates[0].Offset < 100_000)
            .Select(candidate => candidate.Plane)
            .DistinctBy(plane => plane.Name)
            .ToList();
        if (result.Count == 0)
        {
            throw new AdvFormatException("The active Pie/Saw cutting plan is empty.");
        }
        return result;
    }

    private static bool ContainsNamedCut(byte[] source)
    {
        for (int offset = 0; offset < source.Length - 6; offset++)
        {
            if (!source.AsSpan(offset, 3).SequenceEqual("Pie"u8)
                && !source.AsSpan(offset, 3).SequenceEqual("Saw"u8)) continue;
            int current = offset + 3;
            while (current < source.Length && source[current] is >= (byte)'0' and <= (byte)'9') current++;
            if (current == offset + 3 || current >= source.Length || source[current++] != (byte)'-') continue;
            if (current < source.Length && source[current] is >= (byte)'0' and <= (byte)'9') return true;
        }
        return false;
    }

    private static Mesh SliceMesh(Mesh source, CutPlane cut, List<CutPlane> cuts, List<CutGroup> groups)
    {
        Mesh upper = ClipClosedMesh(source, cut.Normal, cut.Distance, keepLessOrEqual: true);
        Mesh slab = ClipClosedMesh(
            upper,
            cut.Normal,
            cut.Distance - cut.Width,
            keepLessOrEqual: false);

        // Advisor pie cuts are made in opposed pairs. Each exported kerf stops
        // at the inner face of its companion cut rather than passing through
        // the complete rough stone.
        if (cut.Name.StartsWith("Pie", StringComparison.Ordinal))
        {
            int dash = cut.Name.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(cut.Name.AsSpan(dash + 1), out int ordinal))
            {
                string prefix = cut.Name[..(dash + 1)];
                // Plans can number Pie records from either zero or one.
                // Determine the origin from this plan's records, so the first
                // zero-based cut is paired with 1 rather than the nonexistent -1.
                int firstOrdinal = cuts.Any(candidate => candidate.Name == prefix + "0") ? 0 : 1;
                int companionOrdinal = ((ordinal - firstOrdinal) & 1) == 0 ? ordinal + 1 : ordinal - 1;
                string companionName = prefix + companionOrdinal;
                CutGroup? group = groups.FirstOrDefault(candidate => candidate.Cuts.Any(item => item.Plane.Name == cut.Name));
                CutPlane? companion = group is not null
                    ? group.Cuts.Single(item => item.Plane.Name != cut.Name).Plane
                    : cuts.FirstOrDefault(candidate => candidate.Name == companionName);
                if (companion is not null)
                {
                    Mesh paired = ClipClosedMesh(slab, companion.Normal,
                        companion.Distance - companion.Width, keepLessOrEqual: false);
                    if (paired.Vertices.Count >= 4 && paired.Faces.Count >= 4)
                    {
                        slab = paired;
                    }
                    else
                    {
                        source.Warnings.Add($"{cut.Name}: its companion boundary removed the entire slice; exported the cut-plane slab for visual review.");
                    }
                }
            }
        }
        slab = ApplyCutHistory(slab, cut, groups);
        if (slab.Vertices.Count < 4 || slab.Faces.Count < 4)
        {
            double minimum = source.Vertices.Min(vertex => Dot(cut.Normal, vertex));
            double maximum = source.Vertices.Max(vertex => Dot(cut.Normal, vertex));
            throw new AdvFormatException(
                $"Cut object {cut.Name} does not intersect the rough mesh "
                + $"(mesh range {minimum:G8}..{maximum:G8}, cut {cut.Distance - cut.Width:G8}..{cut.Distance:G8}).");
        }
        int invalidEdges = CountInvalidEdges(slab.Faces);
        if (invalidEdges > 0)
        {
            var separated = TriangleTopology.SeparateTouchingFans(slab.Vertices.Count, slab.Faces);
            if (separated.VertexSources.Count > slab.Vertices.Count
                && CountInvalidEdges(separated.Faces) == 0)
            {
                int duplicated = separated.VertexSources.Count - slab.Vertices.Count;
                slab = new Mesh(separated.VertexSources.Select(index => slab.Vertices[index]).ToList(),
                    separated.Faces, slab.RepairedVertexCount, slab.Warnings);
                source.Warnings.Add($"{cut.Name}: separated {duplicated} shared vertices at touching surfaces; triangle coordinates are unchanged.");
                invalidEdges = 0;
            }
        }
        if (invalidEdges > 0)
        {
            source.Warnings.Add($"{cut.Name}: {invalidEdges} non-manifold cut edges remain near repaired coordinates; review this mesh in MeshLab.");
        }
        return slab;
    }

    private static Mesh BuildConvexHull(Mesh source, CancellationToken cancellationToken = default)
    {
        List<Vertex> points = source.Vertices;
        if (points.Count < 4) throw new AdvFormatException("The rough mesh has too few vertices.");
        int a = Enumerable.Range(0, points.Count).MinBy(i => points[i].X);
        int b = Enumerable.Range(0, points.Count).MaxBy(i => Distance(points[a], points[i]));
        Vertex ab = Subtract(points[b], points[a]);
        int c = Enumerable.Range(0, points.Count).MaxBy(i => Length(Cross(ab, Subtract(points[i], points[a]))));
        Vertex normal = Cross(ab, Subtract(points[c], points[a]));
        int d = Enumerable.Range(0, points.Count).MaxBy(i => Math.Abs(Dot(normal, Subtract(points[i], points[a]))));
        double scale = points.Max(vertex => Distance(points[a], vertex));
        double epsilon = Math.Max(1e-7, scale * 1e-9);
        if (Length(normal) <= epsilon || Math.Abs(Dot(normal, Subtract(points[d], points[a]))) <= epsilon)
            throw new AdvFormatException("The rough mesh is degenerate.");

        Vertex inside = Multiply(Add(Add(points[a], points[b]), Add(points[c], points[d])), 0.25);
        List<HullFace> faces =
        [
            CreateHullFace(a, b, c, points, inside),
            CreateHullFace(a, d, b, points, inside),
            CreateHullFace(b, d, c, points, inside),
            CreateHullFace(c, d, a, points, inside),
        ];
        HashSet<int> seed = [a, b, c, d];
        for (int pointIndex = 0; pointIndex < points.Count; pointIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seed.Contains(pointIndex)) continue;
            List<HullFace> visible = faces
                .Where(face => Dot(face.Normal, points[pointIndex]) - face.Offset > epsilon)
                .ToList();
            if (visible.Count == 0) continue;
            Dictionary<ulong, (int A, int B)> horizon = [];
            foreach (HullFace face in visible)
            {
                AddHorizonEdge(horizon, face.A, face.B);
                AddHorizonEdge(horizon, face.B, face.C);
                AddHorizonEdge(horizon, face.C, face.A);
            }
            HashSet<HullFace> removed = visible.ToHashSet();
            faces.RemoveAll(removed.Contains);
            foreach ((int edgeA, int edgeB) in horizon.Values)
                faces.Add(CreateHullFace(edgeA, edgeB, pointIndex, points, inside));
        }

        HashSet<int> used = faces.SelectMany(face => new[] { face.A, face.B, face.C }).ToHashSet();
        Dictionary<int, int> remap = [];
        List<Vertex> vertices = [];
        foreach (int oldIndex in used.Order())
        {
            remap[oldIndex] = vertices.Count;
            vertices.Add(points[oldIndex]);
        }
        List<(int A, int B, int C)> triangles = faces
            .Select(face => (remap[face.A], remap[face.B], remap[face.C]))
            .ToList();
        return new Mesh(vertices, triangles, source.RepairedVertexCount, source.Warnings);
    }

    private static HullFace CreateHullFace(int a, int b, int c, List<Vertex> points, Vertex inside)
    {
        Vertex normal = Cross(Subtract(points[b], points[a]), Subtract(points[c], points[a]));
        if (Dot(normal, Subtract(inside, points[a])) > 0)
        {
            (b, c) = (c, b);
            normal = Multiply(normal, -1);
        }
        double length = Length(normal);
        normal = Multiply(normal, 1 / length);
        return new HullFace(a, b, c, normal, Dot(normal, points[a]));
    }

    private static void AddHorizonEdge(Dictionary<ulong, (int A, int B)> edges, int a, int b)
    {
        ulong reverse = ((ulong)(uint)b << 32) | (uint)a;
        if (edges.Remove(reverse)) return;
        edges[((ulong)(uint)a << 32) | (uint)b] = (a, b);
    }

    private static Vertex Add(Vertex a, Vertex b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Vertex Subtract(Vertex a, Vertex b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    private static Vertex Multiply(Vertex value, double scale) => new(value.X * scale, value.Y * scale, value.Z * scale);
    private static Vertex Cross(Vertex a, Vertex b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    private static double Length(Vertex value) => Math.Sqrt(Dot(value, value));

    private static Mesh ClipClosedMesh(Mesh source, Vertex normal, double offset, bool keepLessOrEqual)
    {
        var builder = new MeshBuilder(source.RepairedVertexCount > 0);
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
        ReportApproximateCaps(source, builder);
        return new Mesh(builder.Vertices, builder.Faces, source.RepairedVertexCount, source.Warnings);
    }

    private static int CountInvalidEdges(List<(int A, int B, int C)> faces)
    {
        Dictionary<ulong, int> edges = new(faces.Count * 3 / 2);
        foreach ((int a, int b, int c) in faces)
        {
            AddEdge(edges, a, b);
            AddEdge(edges, b, c);
            AddEdge(edges, c, a);
        }
        return edges.Values.Count(count => count != 2);
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
        contents.Append("[saws]\n");
        contents.Append(cuts.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        foreach (CutPlane cut in cuts)
        {
            token.ThrowIfCancellationRequested();
            contents.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{cut.Name} Width:{cut.Width:G17}")).Append('\n');
        }
        await WriteTextAtomicallyAsync(path, contents.ToString(), token);
    }

    private static Task WriteGalaxySymbolsAsync(
        string path,
        List<GalaxySymbol> symbols,
        CancellationToken token)
    {
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
            if ((variant == 0 && reserved == 0)
                || (variant == 1 && reserved <= 1))
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

        // Prefer an explicitly bounded table to a guessed six-symbol sequence.
        // An ADV can also contain older plans or inactive symbol records.
        List<GalaxyCandidate>? required = FindGuidGalaxyTable(source, candidates)
            ?? FindCountedGalaxyTable(source, candidates)
            ?? FindShiftedVariant2GalaxyTable(source)
            ?? FindGalaxyTableWithInactiveSlot(source, candidates);
        bool hasDeclaredTable = required is not null;
        if (required is null)
        {
            required = SelectGalaxySequence(candidates, 6);
            if (required is null)
            {
                throw new AdvFormatException("The Galaxy symbol coordinate table was not found.");
            }
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
        if (!hasDeclaredTable && optional is not null) required.Add(optional);

        return required
            .OrderBy(candidate => candidate.Ordinal)
            .Select(candidate => new GalaxySymbol(labels[candidate.Ordinal], candidate.Position,
                candidate.Repaired))
            .ToList();
    }

    private static List<GalaxyCandidate>? FindCountedGalaxyTable(
        byte[] source, List<GalaxyCandidate> candidates)
    {
        Dictionary<int, GalaxyCandidate> byOffset = candidates.ToDictionary(
            candidate => candidate.Offset, candidate => candidate);
        List<GalaxyCandidate>? best = null;
        foreach (GalaxyCandidate first in candidates)
        {
            if (first.Offset < 4) continue;
            uint count = ReadUInt32(source, first.Offset - 4);
            if (count is < 3 or > 7 || ReadUInt32(source, first.Offset + 8) != 2) continue;
            List<GalaxyCandidate> table = [];
            for (int index = 0; index < count; index++)
            {
                if (!byOffset.TryGetValue(first.Offset + index * 80, out GalaxyCandidate? entry)
                    || ReadUInt32(source, entry.Offset + 8) != 2)
                {
                    table.Clear();
                    break;
                }
                table.Add(entry);
            }
            if (table.Count != count
                || table.Select(item => item.Ordinal).Distinct().Count() != count
                || table.Select(item => item.Code).Distinct().Count() != count)
            {
                continue;
            }
            if (best is null || table.Count > best.Count) best = table;
        }
        return best;
    }

    private static List<GalaxyCandidate>? FindGuidGalaxyTable(
        byte[] source, List<GalaxyCandidate> candidates)
    {
        int search = Math.Max(0, source.Length - 4_000_000);
        while ((search = FindBytes(source, GalaxyTableSignature, search, source.Length)) >= 0)
        {
            int header = search;
            search++;
            if (header > source.Length - 32) continue;
            uint count = ReadUInt32(source, header + 28);
            if (count is < 3 or > 7) continue;
            int firstOffset = header + 32;
            List<GalaxyCandidate> table = candidates
                .Where(candidate => candidate.Offset >= firstOffset
                    && candidate.Offset <= firstOffset + 500_000
                    && candidate.Code < count
                    && ReadUInt32(source, candidate.Offset + 8) is 1 or 2)
                .Take((int)count)
                .ToList();
            if (table.Count == count
                && table[0].Offset == firstOffset
                && table.Select(item => item.Code).Distinct().Count() == count
                && table.Select(item => item.Ordinal).Distinct().Count() == count)
            {
                return table;
            }
        }
        return null;
    }

    private static List<GalaxyCandidate>? FindShiftedVariant2GalaxyTable(byte[] source)
    {
        int search = Math.Max(0, source.Length - 4_000_000);
        while ((search = FindBytes(source, GalaxyTableSignature, search, source.Length)) >= 0)
        {
            int header = search;
            search++;
            if (header > source.Length - 32) continue;
            uint count = ReadUInt32(source, header + 28);
            if (count is < 3 or > 7 || header + 32L + count * 80 > source.Length) continue;
            List<GalaxyCandidate> table = [];
            for (int index = 0; index < count; index++)
            {
                int expectedOffset = header + 32 + index * 80;
                GalaxyCandidate? record = null;
                foreach (int shift in new[] { 0, -2, 2, -1, 1 })
                {
                    int offset = expectedOffset + shift;
                    if (offset < header + 32 || offset > source.Length - 64) continue;
                    uint code = ReadUInt32(source, offset);
                    uint ordinal = ReadUInt32(source, offset + 4);
                    if (code >= count || ordinal > 6
                        || ReadUInt32(source, offset + 8) != 2
                        || ReadUInt32(source, offset + 12) != 1)
                    {
                        continue;
                    }

                    bool repaired = shift != 0;
                    double[] position = new double[3];
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int coordinateOffset = offset + 40 + axis * 8;
                        double value = ReadDouble(source, coordinateOffset);
                        if (!IsSymbolCoordinate(value))
                        {
                            value = BitConverter.Int64BitsToDouble(
                                unchecked((long)(ReadUInt64(source, coordinateOffset) << 16)));
                            repaired = true;
                        }
                        position[axis] = value;
                    }
                    Vertex vertex = new(position[0], position[1], position[2]);
                    if (!IsGalaxyPosition(vertex)) continue;
                    record = new GalaxyCandidate(offset, (int)code, (int)ordinal, vertex, repaired);
                    break;
                }
                if (record is null)
                {
                    table.Clear();
                    break;
                }
                table.Add(record);
            }
            if (table.Count == count
                && table.Select(item => item.Code).Distinct().Count() == count
                && table.Select(item => item.Ordinal).Distinct().Count() == count
                && table.Any(item => item.Repaired))
            {
                return table;
            }
        }
        return null;
    }

    private static List<GalaxyCandidate>? FindGalaxyTableWithInactiveSlot(
        byte[] source, List<GalaxyCandidate> candidates)
    {
        int search = Math.Max(0, source.Length - 4_000_000);
        while ((search = FindBytes(source, GalaxyTableSignature, search, source.Length)) >= 0)
        {
            int header = search;
            search++;
            if (header > source.Length - 32) continue;
            uint count = ReadUInt32(source, header + 28);
            if (count is < 4 or > 7) continue;
            int first = header + 32;
            int inactiveCode = -1;
            int inactiveOffset = -1;
            for (int offset = first; offset <= Math.Min(source.Length - 80, first + 500_000); offset++)
            {
                uint code = ReadUInt32(source, offset);
                if (code >= count || ReadUInt32(source, offset + 8) != 2
                    || ReadUInt32(source, offset + 12) != 3
                    || !source.AsSpan(offset + 16, 48).SequenceEqual(new byte[48])) continue;
                inactiveCode = (int)code;
                inactiveOffset = offset;
                break;
            }
            if (inactiveCode < 0 || inactiveOffset != first && inactiveOffset > first + count * 80)
                continue;

            List<GalaxyCandidate> table = [];
            for (int code = 0; code < count; code++)
            {
                if (code == inactiveCode) continue;
                GalaxyCandidate? entry = candidates.FirstOrDefault(candidate =>
                    candidate.Code == code && candidate.Offset >= first
                    && candidate.Offset <= first + 500_000
                    && ReadUInt32(source, candidate.Offset + 8) is 1 or 2);
                if (entry is null && inactiveOffset > first)
                {
                    // Variant-2 records occupy fixed 80-byte slots. Their
                    // last coordinate may have lost its low two bytes.
                    int offset = first + code * 80;
                    if (offset <= source.Length - 64
                        && ReadUInt32(source, offset) == code
                        && ReadUInt32(source, offset + 8) == 2
                        && ReadUInt32(source, offset + 12) == 1)
                    {
                        double[] values = new double[3];
                        bool repaired = false;
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int coordinate = offset + 40 + axis * 8;
                            double value = ReadDouble(source, coordinate);
                            if (!IsSymbolCoordinate(value))
                            {
                                value = BitConverter.Int64BitsToDouble(
                                    unchecked((long)(ReadUInt64(source, coordinate) << 16)));
                                repaired = true;
                            }
                            values[axis] = value;
                        }
                        Vertex position = new(values[0], values[1], values[2]);
                        if (IsGalaxyPosition(position))
                            entry = new GalaxyCandidate(offset, code, (int)ReadUInt32(source, offset + 4),
                                position, repaired);
                    }
                }
                if (entry is null)
                {
                    table.Clear();
                    break;
                }
                table.Add(entry);
            }
            if (table.Count == count - 1
                && table.Select(item => item.Code).Distinct().Count() == count - 1
                && table.Select(item => item.Ordinal).Distinct().Count() == count - 1)
                return table;
        }
        return null;
    }

    private static bool HasInactiveGalaxySlot(byte[] source)
    {
        int search = Math.Max(0, source.Length - 4_000_000);
        while ((search = FindBytes(source, GalaxyTableSignature, search, source.Length)) >= 0)
        {
            int header = search;
            search++;
            if (header > source.Length - 32 || ReadUInt32(source, header + 28) != 6) continue;
            int first = header + 32;
            for (int index = 0; index < 6 && first + index * 80 <= source.Length - 64; index++)
            {
                int offset = first + index * 80;
                if (ReadUInt32(source, offset + 8) == 2
                    && ReadUInt32(source, offset + 12) == 3
                    && source.AsSpan(offset + 16, 48).SequenceEqual(new byte[48]))
                    return true;
            }
        }
        return false;
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

    private static bool IsSymbolCoordinate(double value) =>
        double.IsFinite(value) && Math.Abs(value) < 10_000
        && (value == 0 || Math.Abs(value) > 1e-20);

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
        int faceStart;
        int inferredVertexCount = 0;
        if (encodedFaceCount < 100)
        {
            (faceStart, encodedFaceCount, inferredVertexCount) = FindPartialFaceTable(payload)
                ?? throw new AdvFormatException("A recoverable rough-mesh triangle table was not found.");
        }
        else
        {
            faceStart = checked(payload.Length - 4 - encodedFaceCount * 16);
        }

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
            if (inferredVertexCount == 0 || FaceMarkers.Contains(marker))
            {
                if (a < generousLimit) plausibleIndices.Add(a);
                if (b < generousLimit) plausibleIndices.Add(b);
                if (c < generousLimit) plausibleIndices.Add(c);
            }
        }

        if (plausibleIndices.Count < encodedFaceCount * 2)
        {
            throw new AdvFormatException("Too few rough-mesh indices survived for safe repair.");
        }

        int vertexCount = inferredVertexCount > 0
            ? inferredVertexCount
            : checked((int)plausibleIndices.Max() + 1);
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

    private static (int FaceStart, int ReadableFaceCount, int VertexCount)? FindPartialFaceTable(
        byte[] payload)
    {
        for (int offset = 4; offset <= payload.Length - 16; offset++)
        {
            if (ReadUInt32(payload, offset) != 3) continue;
            int end = offset;
            while (end <= payload.Length - 16 && ReadUInt32(payload, end) == 3)
            {
                end += 16;
            }
            int intactCount = (end - offset) / 16;
            uint declaredValue = ReadUInt32(payload, offset - 4);
            if (declaredValue > 2_000_000)
            {
                offset = end - 1;
                continue;
            }
            int declaredCount = (int)declaredValue;
            if (intactCount >= 1000
                && declaredCount >= intactCount
                && declaredCount <= intactCount * 2
                && (declaredCount & 1) == 0)
            {
                int vertexCount = (declaredCount + 4) / 2;
                int vertexStart = offset - 4 - vertexCount * 24;
                if (vertexStart >= 0
                    && Enumerable.Range(0, Math.Min(32, vertexCount))
                        .All(index => IsPlausible(ReadVertex(payload, vertexStart + index * 24))))
                {
                    int readableCount = Math.Min(declaredCount, (payload.Length - offset) / 16);
                    return (offset - 4, readableCount, vertexCount);
                }
            }
            offset = end - 1;
        }
        return null;
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
        int reconstructedVertices = 0;

        if (missingBytes == 0)
        {
            for (int index = 0; index < vertexCount; index++)
            {
                decoded[index] = ReadVertex(payload, vertexStart + index * 24);
            }
        }
        else
        {
            int damagedPrefix = 0;
            while (damagedPrefix < Math.Min(vertexCount - 5, 64)
                   && !IsPlausible(ReadVertex(payload, vertexStart + damagedPrefix * 24)))
            {
                damagedPrefix++;
            }
            if (damagedPrefix > 0)
            {
                if (damagedPrefix >= 64)
                {
                    throw new AdvFormatException("The rough-mesh vertex prefix is too damaged to reconstruct.");
                }
                warnings.Add($"Reconstructed {damagedPrefix} damaged leading {(damagedPrefix == 1 ? "vertex" : "vertices")} from adjacent surface points.");
            }

            int split = -1;
            for (int index = damagedPrefix; index < vertexCount; index++)
            {
                Vertex vertex = ReadVertex(payload, vertexStart + index * 24);
                if (!IsPlausible(vertex))
                {
                    split = index;
                    break;
                }
                decoded[index] = vertex;
            }

            // The original damaged samples replace four vertices with a short
            // marker. Some later files instead lose a few bytes within one
            // vertex. In both cases the surviving suffix is end-aligned.
            int missingVertices = missingBytes <= 24 ? 1 : 4;
            reconstructedVertices = missingVertices;
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
            if (damagedPrefix > 0)
            {
                RepairLeadingVertices(decoded, faces, damagedPrefix);
                for (int index = 0; index < damagedPrefix; index++) repairedVertices.Add(index);
            }
            repairWindowStart = split;
            for (int index = split; index < split + missingVertices; index++)
            {
                repairedVertices.Add(index);
            }
            warnings.Add($"Reconstructed {missingVertices} {(missingVertices == 1 ? "vertex" : "vertices")} from adjacent surface points.");
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
        else if (repairedVertices.Count > reconstructedVertices && warnings.Count > 0)
        {
            warnings.Add($"Repaired coordinates in {repairedVertices.Count - reconstructedVertices} additional vertices.");
        }

        return (decoded.Select(vertex => vertex!.Value).ToList(), repairedVertices.Count, warnings);
    }

    private static int FindBestVertexStart(byte[] payload, int estimate, int faceStart, int vertexCount)
    {
        // The serialized mesh record starts with its byte length and vertex
        // count. Its declared length includes both counts, all vertex triples,
        // and all triangle records, even when bytes inside the record are lost.
        // Use that structural boundary before considering plausible doubles:
        // preceding metadata can itself decode to perfectly finite coordinates.
        long declaredLength = 8L + vertexCount * 24L + (payload.Length - faceStart - 4);
        int structuralStart = -1;
        for (int candidate = Math.Max(8, estimate);
             candidate <= Math.Min(faceStart - 24, estimate + 128); candidate++)
        {
            if (ReadUInt32(payload, candidate - 8) != declaredLength) continue;
            if (structuralStart >= 0)
            {
                throw new AdvFormatException("The rough mesh contains ambiguous vertex-record boundaries.");
            }
            structuralStart = candidate;
        }
        if (structuralStart >= 0) return structuralStart;

        int sampleCount = Math.Min(100, vertexCount);
        int sampleOffset = Math.Min(32, Math.Max(0, vertexCount - sampleCount));
        int minimum = Math.Max(0, estimate);
        int maximum = Math.Min(faceStart - sampleCount * 24, estimate + 128);
        int bestOffset = -1;
        int bestScore = -1;
        int bestDistance = int.MaxValue;

        for (int candidate = minimum; candidate <= maximum; candidate++)
        {
            int score = 0;
            for (int index = 0; index < sampleCount; index++)
            {
                if (IsPlausible(ReadVertex(payload, candidate + (sampleOffset + index) * 24)))
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

    private static void RepairLeadingVertices(
        Vertex?[] vertices, List<(int A, int B, int C)> faces, int count)
    {
        List<int>[] neighbours = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        foreach ((int a, int b, int c) in faces)
        {
            if (a < count) { neighbours[a].Add(b); neighbours[a].Add(c); }
            if (b < count) { neighbours[b].Add(a); neighbours[b].Add(c); }
            if (c < count) { neighbours[c].Add(a); neighbours[c].Add(b); }
        }
        for (int pass = 0; pass < count; pass++)
        {
            bool progressed = false;
            for (int index = 0; index < count; index++)
            {
                if (vertices[index].HasValue) continue;
                List<Vertex> available = neighbours[index]
                    .Where(neighbour => vertices[neighbour].HasValue && IsPlausible(vertices[neighbour]!.Value))
                    .Select(neighbour => vertices[neighbour]!.Value)
                    .ToList();
                if (available.Count == 0) continue;
                vertices[index] = new Vertex(
                    available.Average(vertex => vertex.X),
                    available.Average(vertex => vertex.Y),
                    available.Average(vertex => vertex.Z));
                progressed = true;
            }
            if (!progressed) break;
        }
        if (vertices.Take(count).Any(vertex => !vertex.HasValue))
        {
            throw new AdvFormatException("Damaged leading vertices are disconnected from the surviving rough mesh.");
        }
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

    private sealed record GalaxyCandidate(int Offset, int Code, int Ordinal, Vertex Position,
        bool Repaired = false);

    private sealed record GalaxySymbol(string Label, Vertex Position, bool Repaired = false);

    private sealed record HullFace(int A, int B, int C, Vertex Normal, double Offset);

    private static void ReportApproximateCaps(Mesh source, MeshBuilder builder)
    {
        const string warning = "Some boundaries of the repaired surface could not be triangulated reliably; approximate caps were used and require review.";
        if (builder.ApproximateCapCount > 0 && !source.Warnings.Contains(warning)) source.Warnings.Add(warning);
    }

    private sealed class MeshBuilder(bool allowApproximateCaps = false)
    {
        private const double Quantization = 1_000_000.0;
        private readonly Dictionary<(long X, long Y, long Z), int> _vertexLookup = [];

        public List<Vertex> Vertices { get; } = [];
        public List<(int A, int B, int C)> Faces { get; } = [];
        public int ApproximateCapCount { get; private set; }

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

        public void CapOpenBoundaries((Vertex Normal, double Offset)? firstPlane = null,
            (Vertex Normal, double Offset)? secondPlane = null)
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

                if (firstPlane is { } planeA && secondPlane is { } planeB)
                {
                    int PlaneOfEdge(int index)
                    {
                        Vertex a = Vertices[loop[index]], b = Vertices[loop[(index + 1) % loop.Count]];
                        return Math.Max(Math.Abs(Dot(a, planeA.Normal) - planeA.Offset),
                            Math.Abs(Dot(b, planeA.Normal) - planeA.Offset)) < 1e-5 ? 0 : 1;
                    }
                    int transition = Enumerable.Range(0, loop.Count)
                        .FirstOrDefault(i => PlaneOfEdge(i) != PlaneOfEdge((i + loop.Count - 1) % loop.Count), -1);
                    if (transition >= 0)
                    {
                        List<int> chain = [loop[transition]];
                        for (int step = 0; step < loop.Count; step++)
                        {
                            int index = (transition + step) % loop.Count;
                            int nextIndex = (index + 1) % loop.Count;
                            chain.Add(loop[nextIndex]);
                            if (PlaneOfEdge(index) != PlaneOfEdge(nextIndex))
                            {
                                TriangulateBoundary(chain);
                                chain = [loop[nextIndex]];
                            }
                        }
                        continue;
                    }
                }
                TriangulateBoundary(loop);
            }
        }

        private void TriangulateBoundary(List<int> boundary)
        {
            // Ear clipping preserves concave outlines. A fan about the average
            // point can extend outside the cut and overlap other triangles.
            var triangles = TriangulatePlanarBoundary(boundary, Vertices, out bool complete);
            if (!complete && allowApproximateCaps)
            {
                // Preserve the older recovery behavior only for an already
                // reconstructed surface, and explicitly report its uncertainty.
                int center = Vertices.Count;
                Vertices.Add(new Vertex(boundary.Average(i => Vertices[i].X),
                    boundary.Average(i => Vertices[i].Y), boundary.Average(i => Vertices[i].Z)));
                for (int i = 0; i < boundary.Count; i++)
                    AddFace(boundary[(i + 1) % boundary.Count], boundary[i], center);
                ApproximateCapCount++;
                return;
            }
            foreach (var (a, b, c) in triangles)
                AddFace(a, b, c);
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
