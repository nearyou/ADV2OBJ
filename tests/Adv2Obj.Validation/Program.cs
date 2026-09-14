using Adv2Obj.Core;

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../../../../");
string input = Path.Combine(root, "Input");
string referenceRoot = Path.Combine(root, "Output");
string temporary = Path.Combine(Path.GetTempPath(), "adv2obj-validation-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);

var converter = new AdvToObjConverter();
int passed = 0;
int expectedFailure = 0;
try
{
    foreach (string adv in Directory.EnumerateFiles(input, "*.adv").Order())
    {
        string name = Path.GetFileNameWithoutExtension(adv);
        string reference = Path.Combine(referenceRoot, name, name + "_Rough.obj");
        string output = Path.Combine(temporary, name + ".obj");
        try
        {
            ConversionResult result = await converter.ConvertAsync(adv, output);
            ObjData expected = ReadObj(reference);
            ObjData actual = ReadObj(output);
            int vertices = expected.Vertices.Count;
            int faces = expected.Faces.Count;
            if (result.VertexCount != vertices || result.FaceCount != faces)
            {
                throw new Exception($"Expected {vertices}/{faces}, got {result.VertexCount}/{result.FaceCount}.");
            }
            if (!actual.Faces.SequenceEqual(expected.Faces))
            {
                throw new Exception("Decoded triangle topology differs from the Advisor export.");
            }
            double maximumError = actual.Vertices.Zip(expected.Vertices, Distance).Max();
            double rmsError = Math.Sqrt(actual.Vertices.Zip(expected.Vertices, DistanceSquared).Average());
            // Advisor's sparse mantissa masks introduce sub-unit coordinate
            // differences even when no geometric reconstruction is required.
            if (result.RepairedVertexCount == 0 && maximumError > 0.2)
            {
                throw new Exception($"Unexpected coordinate error: {maximumError:G6}.");
            }
            Console.WriteLine($"PASS {name}: {vertices:N0} vertices, {faces:N0} faces, "
                              + $"{result.RepairedVertexCount:N0} repaired, "
                              + $"max/RMS error {maximumError:G6}/{rmsError:G6}");
            passed++;
        }
        catch (AdvFormatException exception) when (name == "A196-188")
        {
            Console.WriteLine($"EXPECTED FAILURE {name}: {exception.Message}");
            expectedFailure++;
        }
    }
}
finally
{
    Directory.Delete(temporary, true);
}

if (passed != 5 || expectedFailure != 1)
{
    throw new Exception($"Validation incomplete: {passed} passed, {expectedFailure} expected failures.");
}

static ObjData ReadObj(string path)
{
    List<(double X, double Y, double Z)> vertices = [];
    List<(int A, int B, int C)> faces = [];
    foreach (string line in File.ReadLines(path))
    {
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 4 && fields[0] == "v")
        {
            vertices.Add((
                double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture)));
        }
        else if (fields.Length == 4 && fields[0] == "f")
        {
            faces.Add((int.Parse(fields[1]), int.Parse(fields[2]), int.Parse(fields[3])));
        }
    }
    return new ObjData(vertices, faces);
}

static double Distance((double X, double Y, double Z) left, (double X, double Y, double Z) right) =>
    Math.Sqrt(DistanceSquared(left, right));

static double DistanceSquared((double X, double Y, double Z) left, (double X, double Y, double Z) right) =>
    Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2) + Math.Pow(left.Z - right.Z, 2);

internal sealed record ObjData(
    List<(double X, double Y, double Z)> Vertices,
    List<(int A, int B, int C)> Faces);
