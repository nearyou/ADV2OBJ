using Adv2Obj.Core;

if (args is ["--surface-check", string surfaceRoot])
{
    int checkedMeshes = 0;
    int failedMeshes = 0;
    foreach (string file in Directory.EnumerateFiles(surfaceRoot, "*.obj", SearchOption.AllDirectories))
    {
        if (Path.GetRelativePath(surfaceRoot, file).Split(Path.DirectorySeparatorChar)
            .Any(part => part.StartsWith(".adv2obj-", StringComparison.Ordinal))) continue;
        ObjData mesh = ReadObj(file);
        var faces = mesh.Faces.Select(face => (face.A - 1, face.B - 1, face.C - 1)).ToList();
        var separated = TriangleTopology.SeparateTouchingFans(mesh.Vertices.Count, faces);
        checkedMeshes++;
        try
        {
            DecoderChecks.VerifySeparation(faces, separated.VertexSources, separated.Faces);
        }
        catch (Exception error)
        {
            failedMeshes++;
            Console.WriteLine($"FAIL {Path.GetFileName(file)}: {error.Message}");
            continue;
        }
        if (separated.VertexSources.Count != mesh.Vertices.Count)
            Console.WriteLine($"PASS {Path.GetFileName(file)}: shared vertices separated with identical triangle geometry.");
    }
    Console.WriteLine($"Surface checks: {checkedMeshes - failedMeshes} passed; {failedMeshes} require review.");
    Environment.ExitCode = failedMeshes == 0 ? 0 : 1;
    return;
}

if (args is ["--decoder-check"])
{
    await DecoderChecks.RunAsync();
    return;
}

if (args is ["--cleanup-check", string cleanupRoot])
{
    string cleanupTemporary = Directory.CreateTempSubdirectory("adv2obj-cleanup-").FullName;
    try
    {
        string inputDirectory = Path.Combine(cleanupTemporary, "input");
        string outputDirectory = Path.Combine(cleanupTemporary, "output");
        Directory.CreateDirectory(inputDirectory);
        string convertedInput = Path.Combine(inputDirectory, "M110-32219.adv");
        File.Copy(Path.Combine(cleanupRoot, "Input", "M110-32219.adv"), convertedInput);

        AdvToObjConverter cleanupConverter = new();
        ConversionAndCleanupResult completed = await cleanupConverter.ConvertAndRemoveInputAsync(
            convertedInput, outputDirectory);
        string convertedDirectory = Path.Combine(outputDirectory, "M110-32219");
        if (!completed.InputRemoved || File.Exists(convertedInput)
            || Directory.GetFiles(convertedDirectory, "*.obj").Length != completed.Conversion.ObjectFileCount
            || Directory.GetFiles(convertedDirectory, "*.csv").Length != 1
            || Directory.GetFiles(convertedDirectory, "*.ini").Length != 1)
        {
            throw new Exception("A completed conversion did not retain its output and remove its input.");
        }

        string failedInput = Path.Combine(inputDirectory, "failed.adv");
        await File.WriteAllBytesAsync(failedInput, [1, 2, 3]);
        bool conversionFailed = false;
        try
        {
            await cleanupConverter.ConvertAndRemoveInputAsync(failedInput, outputDirectory);
        }
        catch (AdvFormatException)
        {
            conversionFailed = true;
        }
        if (!conversionFailed || !File.Exists(failedInput))
        {
            throw new Exception("A failed conversion removed its input.");
        }

        string cancelledInput = Path.Combine(inputDirectory, "cancelled.adv");
        File.Copy(Path.Combine(cleanupRoot, "Input", "M110-32219.adv"), cancelledInput);
        bool conversionCancelled = false;
        try
        {
            await cleanupConverter.ConvertAndRemoveInputAsync(
                cancelledInput, outputDirectory, new CancellationToken(true));
        }
        catch (OperationCanceledException)
        {
            conversionCancelled = true;
        }
        if (!conversionCancelled || !File.Exists(cancelledInput))
        {
            throw new Exception("A cancelled conversion removed its input.");
        }
        Console.WriteLine("PASS input cleanup: completed input removed; failed and cancelled inputs retained.");
    }
    finally
    {
        Directory.Delete(cleanupTemporary, true);
    }
    return;
}

if (args is ["--probe-file", string singleInput, string singleOutput])
{
    ConversionResult result = await new AdvToObjConverter().ConvertAsync(singleInput, singleOutput);
    Console.WriteLine($"OK {Path.GetFileName(singleInput)}: {result.ObjectFileCount} OBJ; "
        + string.Join(" ", result.Warnings));
    return;
}

if (args is ["--probe", string probeInput, string probeOutput])
{
    var probeConverter = new AdvToObjConverter();
    int failures = 0;
    foreach (string advFile in Directory.EnumerateFiles(probeInput, "*.adv").Order())
    {
        try
        {
            ConversionResult result = await probeConverter.ConvertAsync(advFile, probeOutput);
            Console.WriteLine($"OK {Path.GetFileName(advFile)}: {result.ObjectFileCount} OBJ; "
                + string.Join(" ", result.Warnings));
        }
        catch (Exception error)
        {
            failures++;
            Console.WriteLine($"FAIL {Path.GetFileName(advFile)}: {error.GetType().Name}: {error.Message}");
        }
    }
    Environment.ExitCode = failures == 0 ? 0 : 1;
    return;
}

string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("../../../../");
string input = Path.Combine(root, "Input");
string referenceRoot = Path.Combine(root, "Output");
string? retainedOutput = Environment.GetEnvironmentVariable("ADV2OBJ_VALIDATION_OUTPUT");
string temporary = string.IsNullOrWhiteSpace(retainedOutput)
    ? Path.Combine(Path.GetTempPath(), "adv2obj-validation-" + Guid.NewGuid().ToString("N"))
    : Path.GetFullPath(retainedOutput);
Directory.CreateDirectory(temporary);

var converter = new AdvToObjConverter();
int passed = 0;
int failed = 0;
try
{
    foreach (string adv in Directory.EnumerateFiles(input, "*.adv").Order()
                 .Where(path => args.Length < 2 || Path.GetFileNameWithoutExtension(path) == args[1]))
    {
        string name = Path.GetFileNameWithoutExtension(adv);
        try
        {
        string referenceDirectory = Path.Combine(referenceRoot, name);
        string reference = Path.Combine(referenceDirectory, name + "_Rough.obj");
            string preexistingDirectory = Path.Combine(temporary, name);
            Directory.CreateDirectory(preexistingDirectory);
            await File.WriteAllTextAsync(Path.Combine(preexistingDirectory, "existing-file.test"), "preserve");
            ConversionResult result = await converter.ConvertAsync(adv, temporary);
            string outputDirectory = Path.Combine(temporary, name);
            string output = Path.Combine(outputDirectory, name + "_Rough.obj");
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
            string[] expectedObjects = Directory.GetFiles(referenceDirectory, "*.obj")
                .Select(Path.GetFileName).Order().ToArray()!;
            string[] actualObjects = Directory.GetFiles(outputDirectory, "*.obj")
                .Select(Path.GetFileName).Order().ToArray()!;
            if (!actualObjects.SequenceEqual(expectedObjects))
            {
                throw new Exception("The generated OBJ file set differs from the reference export. "
                    + "Expected: " + string.Join(", ", expectedObjects)
                    + "; actual: " + string.Join(", ", actualObjects));
            }
            if (!File.Exists(Path.Combine(outputDirectory, name + "_GalaxySymbols.csv"))
                || !File.Exists(Path.Combine(outputDirectory, name + "_SawsMD.ini")))
            {
                throw new Exception("A companion CSV or INI file is missing.");
            }
            string[] expectedSymbols = File.ReadAllLines(Path.Combine(referenceDirectory, name + "_GalaxySymbols.csv"));
            string[] actualSymbols = File.ReadAllLines(Path.Combine(outputDirectory, name + "_GalaxySymbols.csv"));
            if (!actualSymbols.SequenceEqual(expectedSymbols))
            {
                throw new Exception("The GalaxySymbols CSV differs from the reference export. "
                    + "Actual: " + string.Join(" | ", actualSymbols));
            }
            string[] expectedIni = File.ReadAllLines(Path.Combine(referenceDirectory, name + "_SawsMD.ini"));
            string[] actualIni = File.ReadAllLines(Path.Combine(outputDirectory, name + "_SawsMD.ini"));
            if (actualIni.Length != expectedIni.Length
                || actualIni[0] != expectedIni[0]
                || actualIni[1] != expectedIni[1]
                || !actualIni.Skip(2).Order().SequenceEqual(expectedIni.Skip(2).Order()))
            {
                throw new Exception("The SawsMD INI differs from the reference export.");
            }
            if (!File.Exists(Path.Combine(outputDirectory, "existing-file.test")))
            {
                throw new Exception("Conversion removed an unrelated pre-existing output file.");
            }
            foreach (string expectedFile in Directory.GetFiles(referenceDirectory))
            {
                string actualFile = Path.Combine(outputDirectory, Path.GetFileName(expectedFile));
                if (!File.Exists(actualFile)
                    || !File.ReadAllBytes(actualFile).SequenceEqual(File.ReadAllBytes(expectedFile)))
                {
                    throw new Exception($"{Path.GetFileName(expectedFile)} differs from the Advisor reference export.");
                }
            }
            foreach (string plannedObject in actualObjects.Where(file => !file.Contains("_Rough.")))
            {
                ObjData planned = ReadObj(Path.Combine(outputDirectory, plannedObject));
                ObjData plannedReference = ReadObj(Path.Combine(referenceDirectory, plannedObject));
                if (planned.Vertices.Count < 4 || planned.Faces.Count != planned.Vertices.Count * 2 - 4)
                {
                    throw new Exception($"{plannedObject} is not a closed triangular surface.");
                }
                Console.WriteLine($"  {plannedObject}: generated {planned.Vertices.Count:N0}/{planned.Faces.Count:N0}; "
                    + $"reference {plannedReference.Vertices.Count:N0}/{plannedReference.Faces.Count:N0}");
            }
            Console.WriteLine($"PASS {name}: {actualObjects.Length:N0} OBJ files; rough {vertices:N0} vertices, {faces:N0} faces, "
                              + $"{result.RepairedVertexCount:N0} repaired, "
                              + $"max/RMS error {maximumError:G6}/{rmsError:G6}");
            passed++;
        }
        catch (Exception error)
        {
            failed++;
            Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}");
        }
    }
}
finally
{
    if (string.IsNullOrWhiteSpace(retainedOutput)) Directory.Delete(temporary, true);
}

Console.WriteLine($"Reference comparison: {passed} matched; {failed} differed or failed.");
Environment.ExitCode = failed == 0 && (args.Length >= 2 || passed == 6) ? 0 : 1;

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
