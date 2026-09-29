using System.IO.Compression;

namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static Mesh? RecoverCorroboratedRoughCopy(byte[] source, byte[] primary, CancellationToken token)
    {
        if (primary.Length < 32) return null;
        int first = FindBytes(primary, PolygonMeshSignature, 0, primary.Length);
        if (first < 0 || first + 28 > primary.Length) return null;
        int next = FindBytes(primary, PolygonMeshSignature, first + 28, primary.Length);
        long length = ReadUInt32(primary, first + 20);
        if (next > first && (next - first - 40) % 38 == 0) length = next - first - 24;
        if (length < 16 || (length - 16) % 38 != 0) return null;
        int count = checked((int)((length - 16) / 38));
        if (count is < 8 or > 2048 || first + 28L + count * 24 > primary.Length) return null;
        int regionEnd = next > first ? next : (int)Math.Min(primary.Length, first + 24L + length);
        ReadOnlySpan<byte> evidence = primary.AsSpan(first + 28, regionEnd - first - 28);
        int cursor = checked((int)ReadUInt32(source, 0x34));
        bool skippedPrimary = false;
        while ((cursor = FindBytes(source, ZipSignature, cursor, source.Length)) >= 0)
        {
            token.ThrowIfCancellationRequested();
            int header = cursor++;
            if (header + 30 > source.Length || ReadUInt16(source, header + 8) != 8) continue;
            long size = ReadUInt32(source, header + 18), expanded = ReadUInt32(source, header + 22);
            long start = header + 30L + ReadUInt16(source, header + 26) + ReadUInt16(source, header + 28);
            if (size <= 0 || expanded < 50_000 || expanded > 64_000_000 || start + size > source.Length) continue;
            if (!skippedPrimary) { skippedPrimary = true; continue; }
            byte[]? payload = InflateRestart(source.AsSpan((int)start, (int)size).ToArray(), 64_000_000);
            if (payload is null || payload.Length < 32) continue;
            Mesh? candidate = TryRecoverPairedPolygonSurface(payload, token);
            if (candidate is null || candidate.Vertices.Count != count) continue;
            // A plan mesh is not interchangeable with the primary rough. Bind
            // a later copy to this record with the same count and >=90% exact
            // XYZ byte triples present in the bounded primary polygon record.
            // Searching bytes also tolerates small gaps before intact triples.
            int matched = 0;
            foreach (Vertex v in candidate.Vertices)
            {
                byte[] xyz = new byte[24];
                BitConverter.GetBytes(v.X).CopyTo(xyz, 0); BitConverter.GetBytes(v.Y).CopyTo(xyz, 8); BitConverter.GetBytes(v.Z).CopyTo(xyz, 16);
                if (evidence.IndexOf(xyz) >= 0) matched++;
            }
            if (matched < Math.Max(8, count * .90)) continue;
            candidate.Warnings.Add($"Recovered another stored copy of the same rough surface: {matched}/{count} exact coordinate triples corroborated by the primary record. Requires visual review.");
            return candidate;
        }
        return null;
    }

    private static byte[] ReadPrimaryInflatePrefix(byte[] source)
    {
        int metadata = checked((int)ReadUInt32(source, 0x34));
        int header = FindBytes(source, ZipSignature, metadata, Math.Min(source.Length, metadata + 1024));
        if (header < 0 || header + 30 > source.Length || ReadUInt16(source, header + 8) != 8) return [];
        long size = ReadUInt32(source, header + 18), start = header + 30L + ReadUInt16(source, header + 26) + ReadUInt16(source, header + 28);
        if (start + size > source.Length) return [];
        using var result = new MemoryStream();
        try
        {
            using var stream = new DeflateStream(new MemoryStream(source, (int)start, (int)size), CompressionMode.Decompress);
            byte[] chunk = new byte[256];
            int read;
            while ((read = stream.Read(chunk)) > 0 && result.Length + read <= 64_000_000) result.Write(chunk, 0, read);
        }
        catch (InvalidDataException) { }
        return result.ToArray();
    }
}
