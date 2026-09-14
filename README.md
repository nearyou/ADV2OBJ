# ADV2OBJ

Windows batch converter for extracting the rough triangular mesh from Sarine
Advisor `.adv` files and writing standard Wavefront `.obj` files.

## User workflow

1. Select the input directory containing `.adv` files.
2. Select the output directory.
3. Click **Convert**.
4. Review per-file completion, repair, or failure details in the status table.

Each input file produces `<input-name>.obj` directly in the selected output
directory. Existing files with the same name are replaced only after a complete
new OBJ has been written to a temporary file.

## Build and run

```powershell
dotnet build Adv2Obj.slnx -c Release
dotnet run --project src/Adv2Obj.App/Adv2Obj.App.csproj
```

The app targets .NET 8 on Windows and has no third-party package dependencies.
After publishing, launch `artifacts/ADV2OBJ/ADV2OBJ.exe`. The target computer
must have the .NET 8 Desktop Runtime installed.

## Sample validation

```powershell
dotnet run --project tests/Adv2Obj.Validation/Adv2Obj.Validation.csproj -- .
```

The supplied paired exports establish these format facts:

- ADV uses the 16-byte signature `CA 9B 28 C7 D7 EC 9B 45 AA BF E4 42 3F EF 0E FF`.
- The primary rough-mesh record is an embedded raw-DEFLATE ZIP member named
  `ZippedData`.
- Coordinates are stored as little-endian 64-bit floating-point triples.
- Faces are stored as a polygon-size value (`3`) followed by three zero-based
  32-bit vertex indices.
- The sample rough meshes are closed triangular surfaces satisfying
  `faces = 2 × vertices − 4` and every undirected edge is shared by two faces.

## Current compatibility boundary

The converter extracts the **rough exterior mesh only**. It does not export the
planned Pie/Saw objects, inclusions, Galaxy symbols, or planning metadata.

Five supplied samples contain recoverable rough mesh records. Some carry sparse
Advisor bit masks or damaged coordinates; their topology is recovered exactly
and damaged coordinates are interpolated with a visible `Completed*` status.
`A196-188.adv` contains a broken DEFLATE stream and is intentionally reported as
failed instead of writing an untrustworthy mesh.

Because ADV is proprietary and the samples show multiple damaged/variant record
layouts, broader production compatibility requires more samples from every
Advisor version and scanner workflow that must be supported.
