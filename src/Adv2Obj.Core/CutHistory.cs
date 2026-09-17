using System.Text;

namespace Adv2Obj.Core;

public sealed partial class AdvToObjConverter
{
    // Serialized cut collection and plane type IDs, not model identifiers.
    private static readonly byte[] CutGroupSignature =
        Convert.FromHexString("A0C08CABAB842A4692BCD35D2AFAADCB");
    private static readonly byte[] CutRecordSignature =
        Convert.FromHexString("2865304F9501CB43B95997C5EA20E0F3");

    private sealed record GroupCut(CutPlane Plane, uint Branch);
    private sealed record CutGroup(List<GroupCut> Cuts, int Piece);

    private static List<CutGroup> DecodeCutGroups(byte[] data, List<CutPlane> activeCuts)
    {
        if (activeCuts.Count == 0) return [];
        var names = activeCuts.Select(cut => cut.Name).ToHashSet(StringComparer.Ordinal);
        List<CutGroup> groups = [];
        HashSet<string> found = [];
        int position = 0;
        while (position <= data.Length - 36)
        {
            int relative = data.AsSpan(position).IndexOf(CutGroupSignature);
            if (relative < 0) break;
            int start = position + relative;
            position = start + 16;
            uint length = ReadUInt32(data, start + 20);
            if (length < 12 || length > 100_000 || start + 24L + length > data.Length) continue;
            int end = start + 24 + (int)length;
            int piece = unchecked((int)ReadUInt32(data, end - 4));
            uint count = ReadUInt32(data, start + 24);
            if (piece < 0 || count == 0 || count > activeCuts.Count) continue;
            List<GroupCut> records = [];
            int cursor = start + 28;
            for (int i = 0; i < count; i++)
            {
                if (cursor + 108 > end || !data.AsSpan(cursor, 16).SequenceEqual(CutRecordSignature)) break;
                uint recordLength = ReadUInt32(data, cursor + 20);
                int nameLength = (int)ReadUInt32(data, cursor + 80);
                if (nameLength < 6 || nameLength > 24 || recordLength != 78 + nameLength
                    || cursor + 28L + recordLength > end - 8) break;
                string name = Encoding.ASCII.GetString(data, cursor + 84, nameLength);
                if (!names.Contains(name)) break;
                double width = ReadDouble(data, cursor + 28);
                double distance = ReadDouble(data, cursor + 36);
                Vertex normal = new(ReadDouble(data, cursor + 52), ReadDouble(data, cursor + 60), ReadDouble(data, cursor + 68));
                if (!double.IsFinite(width) || width is < 1 or > 500 || !double.IsFinite(distance)
                    || !double.IsFinite(Length(normal)) || Math.Abs(Length(normal) - 1) > .02) break;
                uint branch = ReadUInt32(data, cursor + 24 + (int)recordLength);
                records.Add(new(new(name, activeCuts[0].PlanNumber, width, distance, normal), branch));
                cursor += 28 + (int)recordLength;
            }
            if (records.Count != count || cursor != end - 8) continue;
            bool pie = records.All(item => item.Plane.Name.StartsWith("Pie", StringComparison.Ordinal));
            if (pie ? records.Count != 2 : records.Any(item => !item.Plane.Name.StartsWith("Saw", StringComparison.Ordinal))) continue;
            if (records.Any(item => found.Contains(item.Plane.Name)))
            {
                groups.Clear();
                found.Clear();
            }
            groups.Add(new(records, piece));
            foreach (var item in records) found.Add(item.Plane.Name);
            if (found.SetEquals(names)) return groups;
            position = end;
        }
        return [];
    }

    private static Mesh ApplyCutHistory(Mesh slice, CutPlane cut, List<CutGroup> groups)
    {
        CutGroup? owner = groups.FirstOrDefault(group => group.Cuts.Any(item => item.Plane.Name == cut.Name));
        if (owner is null) return slice;
        bool isPie = cut.Name.StartsWith("Pie", StringComparison.Ordinal);
        if (!isPie && owner.Piece != 0 && !groups.Any(group => group.Piece == owner.Piece
            && group.Cuts[0].Plane.Name.StartsWith("Pie", StringComparison.Ordinal)))
            throw new AdvFormatException($"The source piece for {cut.Name} is missing from its cutting plan.");
        foreach (CutGroup group in groups.Where(group => group.Cuts[0].Plane.Name.StartsWith("Pie", StringComparison.Ordinal)))
        {
            if (isPie && group == owner) break;
            CutPlane a = group.Cuts[0].Plane, b = group.Cuts[1].Plane;
            if (!isPie && owner.Piece == group.Piece)
            {
                slice = ClipClosedMesh(slice, a.Normal, a.Distance, false);
                slice = ClipClosedMesh(slice, b.Normal, b.Distance, false);
                break;
            }
            slice = SubtractWedge(slice, a, b);
        }
        if (!isPie)
        {
            // The stored branch is a zero-based binary-tree position: root 0,
            // positive child 2*n+1, negative child 2*n+2. Each branch excludes
            // its parent's kerf, using the appropriate outer/inner plane.
            uint branch = owner.Cuts.Single(item => item.Plane.Name == cut.Name).Branch;
            while (branch > 0)
            {
                uint parent = (branch - 1) / 2;
                GroupCut? ancestor = owner.Cuts.FirstOrDefault(item => item.Branch == parent);
                if (ancestor is null) throw new AdvFormatException($"The parent cut for {cut.Name} is missing from its cutting tree.");
                bool lesserSide = (branch & 1) == 0;
                slice = ClipClosedMesh(slice, ancestor.Plane.Normal,
                    ancestor.Plane.Distance - (lesserSide ? ancestor.Plane.Width : 0), lesserSide);
                branch = parent;
            }
        }
        return slice;
    }

    private static Mesh SubtractWedge(Mesh source, CutPlane a, CutPlane b)
    {
        double da = a.Distance - a.Width, db = b.Distance - b.Width;
        var builder = new MeshBuilder(source.RepairedVertexCount > 0);
        foreach (var (i, j, k) in source.Faces)
        {
            List<Vertex> triangle = [source.Vertices[i], source.Vertices[j], source.Vertices[k]];
            // Split both sides at both planes so adjacent polygons share vertices.
            foreach (bool lessA in new[] { true, false })
            foreach (bool lessB in new[] { true, false })
            {
                if (!lessA && !lessB) continue;
                var polygon = ClipPolygon(ClipPolygon(triangle, a.Normal, da, lessA), b.Normal, db, lessB);
                for (int t = 1; t + 1 < polygon.Count; t++)
                    builder.AddFace(builder.AddVertex(polygon[0]), builder.AddVertex(polygon[t]), builder.AddVertex(polygon[t + 1]));
            }
        }
        builder.CapOpenBoundaries((a.Normal, da), (b.Normal, db));
        ReportApproximateCaps(source, builder);
        return new(builder.Vertices, builder.Faces, source.RepairedVertexCount, source.Warnings);
    }
}
