# ADV2OBJ

Windows batch converter for extracting Sarine Advisor `.adv` geometry into a
per-stone directory of Wavefront `.obj` files plus companion `.csv` and `.ini`
files.

## User workflow

1. Select the input directory containing `.adv` files.
2. Select the output directory.
3. Click **Convert**.
4. Review per-file completion, repair, or failure details in the status table.

Each input file produces this structure:

```text
<selected-output>\<input-name>\
  <input-name>_Rough.obj
  <input-name>_Pie<number>-<number>.obj   (one or more)
  <input-name>_Saw<number>-<number>.obj   (one or more)
  <input-name>_GalaxySymbols.csv
  <input-name>_SawsMD.ini
```

Each file is written directly to its final filename with an exclusive stream
that is closed before the next file starts. This avoids temporary-file rename
failures and file-lock collisions on watched or synchronized output drives.

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
- Active Pie/Saw entries store a saw width, plane distance, and unit normal.
  Each planned OBJ is reconstructed as the closed portion of the rough mesh
  between the entry's two parallel planes.
- The sample rough meshes are closed triangular surfaces satisfying
  `faces = 2 × vertices − 4` and every undirected edge is shared by two faces.

## Current compatibility boundary

The converter decodes the indexed Galaxy symbol table (`X`, `T`, `V`, `(X)`,
`(T)`, `(V)`, and optional `K`) and writes its coordinates with the same numeric
format as the supplied reference CSV files. Rough and planned Pie/Saw OBJ
geometry and the SawsMD INI entries are also generated from the ADV itself.

Five supplied samples contain recoverable rough mesh records. Some carry sparse
Advisor bit masks or damaged coordinates; their topology is recovered exactly
and damaged coordinates are interpolated with a visible `Completed*` status.
`A196-188.adv` contains a broken DEFLATE stream. The converter attempts a narrow,
validated one-bit repair and otherwise reports the file as failed instead of
writing an untrustworthy mesh. The other five supplied samples convert into the
same OBJ filename sets as their reference directories; every generated planned
mesh is a closed triangular surface.

Because ADV is proprietary and the samples show multiple damaged/variant record
layouts, broader production compatibility requires more samples from every
Advisor version and scanner workflow that must be supported.
