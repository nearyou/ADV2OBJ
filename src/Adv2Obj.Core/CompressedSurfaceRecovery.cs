using System.IO.Compression;

namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    private static Mesh? RecoverCompressedStoredSurface(byte[] source, ref int recordEnd, CancellationToken token)
    {
        int metadata = checked((int)ReadUInt32(source, 0x34));
        int name = FindBytes(source, ZippedDataName, metadata, Math.Min(source.Length, metadata + 1024));
        if (name < 22) return null;
        int header = name - 22;
        uint size = ReadUInt32(source, header + 10), expected = ReadUInt32(source, header + 14);
        long begin = name + (long)ZippedDataName.Length + ReadUInt16(source, header + 20);
        if (size is < 1024 or > 16_000_000 || expected is < 1024 or > 32_000_000 || begin + size > source.Length) return null;
        byte[] compressed = source.AsSpan((int)begin, (int)size).ToArray();
        int failure = FindInflateFailureOffset(compressed);
        int first = Math.Max(0, failure - 8192), last = Math.Min(compressed.Length - 513, failure + 32768);
        byte[] probe = SeededDeflateBuffer(512, 0);
        for (int bits = 0; bits < 8; bits++)
        {
            byte[] shifted = new byte[compressed.Length - 1];
            for (int i = 0; i < shifted.Length; i++)
                shifted[i] = (byte)((compressed[i] >> bits) | (compressed[i + 1] << (8 - bits)));
            for (int offset = first; offset <= last; offset++)
            {
                if ((offset & 1023) == 0) token.ThrowIfCancellationRequested();
                if ((shifted[offset] & 6) != 4) continue; // Dynamic Huffman block.
                shifted.AsSpan(offset, 512).CopyTo(probe.AsSpan(32773));
                if (InflateRestart(probe, 32768 + 131072) is not { Length: > 32768 }) continue;
                byte[] seeded = SeededDeflateBuffer(shifted.Length - offset, 0);
                shifted.AsSpan(offset).CopyTo(seeded.AsSpan(32773));
                byte[]? low = InflateRestart(seeded, checked((int)expected + 32768 + 4096));
                if (low is null || FindBytes(low, PolygonMeshSignature, 32768, low.Length) < 0) continue;
                seeded.AsSpan(5, 32768).Fill(255);
                byte[]? high = InflateRestart(seeded, checked((int)expected + 32768 + 4096));
                if (high is null || high.Length != low.Length) continue;
                byte[] payload = low.AsSpan(32768).ToArray();
                bool[] unknown = Enumerable.Range(32768, payload.Length).Select(i => low[i] != high[i]).ToArray();
                if (!ExcludeUnknownPolygonData(payload, unknown)) continue;
                Mesh? surface = TryRecoverStoredPolygon(payload, token);
                if (surface is null) continue;
                surface.Warnings.Add("Recovered a later DEFLATE block after unreadable compressed bytes. Dictionary-dependent coordinates were excluded and reconstructed from stored polygon planes; requires visual review.");
                recordEnd = checked((int)(begin + size));
                return surface;
            }
        }
        return null;
    }

    // A non-final stored block supplies a synthetic history to the raw inflater.
    // Inflating with two different histories identifies every byte that still
    // depends on unavailable data: DEFLATE only copies dictionary bytes.
    private static byte[] SeededDeflateBuffer(int suffixLength, byte value)
    {
        byte[] buffer = new byte[32773 + suffixLength];
        buffer[2] = 128; buffer[3] = 255; buffer[4] = 127;
        buffer.AsSpan(5, 32768).Fill(value);
        return buffer;
    }

    private static byte[]? InflateRestart(byte[] data, int limit)
    {
        try
        {
            using var stream = new DeflateStream(new MemoryStream(data, false), CompressionMode.Decompress);
            using var result = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk)) > 0)
            {
                if (result.Length + read > limit) return null;
                result.Write(chunk, 0, read);
            }
            return result.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    private static bool ExcludeUnknownPolygonData(byte[] payload, bool[] unknown)
    {
        var headers = new List<int>();
        int cursor = 0;
        while ((cursor = FindBytes(payload, PolygonMeshSignature, cursor, payload.Length)) >= 0)
        {
            if (unknown.AsSpan(cursor, 16).Contains(true)) return false;
            headers.Add(cursor++);
        }
        if (headers.Count != 3 || payload[headers[2] - 1] != 1) return false;
        for (int copy = 0; copy < 2; copy++)
        {
            int start = headers[copy], end = headers[copy + 1] - (copy == 1 ? 1 : 0);
            var record = ReadPolygonRecord(payload, start, end);
            if (record is null || unknown[start + 24]) return false;
            int count = record.Vertices.Count;
            for (int i = 0; i < count * 3; i++)
            {
                int position = start + 28 + i * 8;
                if (unknown.AsSpan(position, 8).Contains(true))
                    BitConverter.GetBytes(double.NaN).CopyTo(payload, position);
            }
            int positionFaces = start + 28 + count * 24;
            if (unknown[positionFaces]) return false;
            positionFaces += 4;
            foreach (var polygon in record.Polygons)
            {
                if (unknown[positionFaces]) return false;
                for (int i = 0; i < polygon.Length; i++)
                {
                    int index = positionFaces + 4 + i * 4;
                    if (unknown.AsSpan(index, count <= 256 ? 1 : 2).Contains(true))
                        BitConverter.GetBytes(uint.MaxValue).CopyTo(payload, index);
                }
                positionFaces += 4 + polygon.Length * 4;
            }
        }
        return true;
    }
}
