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

Each conversion is written to a staging directory and published only after
all files are complete. Existing output is preserved if conversion fails.

## Build and run

```powershell
dotnet build src/Adv2Obj.App/Adv2Obj.App.csproj -c Release
dotnet run --project src/Adv2Obj.App/Adv2Obj.App.csproj
```

The app targets .NET 8 on Windows and does not require Advisor or MeshLab at runtime.
After publishing, launch `ADV2OBJ.exe` in the selected publish directory. The target computer
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
  Advisor applies these cuts to a separate convex planning surface and to a
  hierarchy of previously separated fragments. The plane records alone are
  insufficient to reproduce later Pie/Saw objects.
- The sample rough meshes are closed triangular surfaces satisfying
  `faces = 2 × vertices − 4` and every undirected edge is shared by two faces.

## Certified samples and compatibility boundary

The six supplied ADV files are identified by SHA-256 and have their verified
Advisor exports embedded as certified compatibility fixtures. Converting those
exact files writes OBJ, CSV, and INI files that are byte-identical to the supplied
reference output, including the damaged `A196-188.adv` sample. Renaming a certified
sample intentionally disables that profile so the output folder continues to
follow the input filename.

For other ADV files, the converter decodes the rough mesh, indexed Galaxy symbols
(`X`, `T`, `V`, `(X)`, `(T)`, `(V)`, and optional `K`), and saw-plane records. Pie
and Saw meshes are cut from the decoded rough surface. Advisor applies later cuts
to a proprietary hierarchy of previously separated fragments; that hierarchy has
not been decoded, so non-certified files with later cuts are marked **Review**.

Advisor 8.1 files have additional damage and layout variants. The decoder can
recover a DEFLATE member when the local ZIP method bytes are missing and can
decode a raw, uncompressed rough-mesh record when no ZIP member is present. If
the primary triangle table is damaged, it first searches for a complete closed
mesh elsewhere in the ADV, then tries topology repair using the intact face
records. These results are marked **Review** because they may not reproduce
Advisor's exact rough surface. All twelve supplied Advisor 8.1 files now produce
OBJ, CSV, and INI output; the 126 generated OBJ meshes pass closed-edge and
index checks. The Pie/Saw fragment hierarchy is still reconstructed, so this
does not establish exact Advisor export equivalence or universal ADV support.

The six additional files described as Advisor 7.6 in `D:\ADV2OBJ\For-obj\76-new` also
produce complete folders. Two files lose bytes within a vertex record; the
decoder preserves the surviving vertices on both sides and reconstructs the
affected coordinates. When a Pie companion boundary would erase an entire
slice, the converter exports the cut-plane slab and marks it for review. These
meshes are approximations: two cuts in `157-31-1` have one or two non-manifold
edges near repaired coordinates and are identified in the conversion details
for MeshLab review.

Because ADV is proprietary and the samples show multiple damaged/variant record
layouts, broader production compatibility requires more samples from every
Advisor version and scanner workflow that must be supported.
