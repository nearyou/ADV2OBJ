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
  <input-name>_Pie<number>-<number>.obj   (when a Pie plan is present)
  <input-name>_Saw<number>-<number>.obj   (when a Saw plan is present)
  <input-name>_GalaxySymbols.csv
  <input-name>_SawsMD.ini
```

Each conversion is written to a staging directory and published only after
all files are complete. Existing output is preserved if conversion fails.
An ADV with no named Pie/Saw plan exports just the rough OBJ; its INI contains
`[saws]` and `0`.
After a successful publish, the app deletes that source `.adv` file. Failed or
cancelled inputs remain in place. If the source changed during conversion or
cannot be deleted, the completed output remains and the row says **Input retained**.

## Build and run

```powershell
dotnet build src/Adv2Obj.App/Adv2Obj.App.csproj -c Release
dotnet run --project src/Adv2Obj.App/Adv2Obj.App.csproj
```

To create the copyable single-file Windows release:

```powershell
dotnet publish src/Adv2Obj.App/Adv2Obj.App.csproj -c Release -p:PublishProfile=SingleFile
```

The release is `artifacts/ADV2OBJ-single/ADV2OBJ.exe`. Copy that file alone to a
64-bit Windows computer; it includes the .NET 8 Desktop Runtime and the app's
resources. Advisor and MeshLab are not required to run the converter.

## Sample validation

```powershell
dotnet run --project tests/Adv2Obj.Validation/Adv2Obj.Validation.csproj -- .
```

This compares generated files against the supplied Advisor exports and exits
with a failure status for differences or conversion errors. It reports each
sample separately so decoder gaps remain visible.

The supplied paired exports establish these format facts:

- ADV uses the 16-byte signature `CA 9B 28 C7 D7 EC 9B 45 AA BF E4 42 3F EF 0E FF`.
- The primary rough-mesh record is an embedded raw-DEFLATE ZIP member named
  `ZippedData`.
- Coordinates are stored as little-endian 64-bit floating-point triples.
- Faces are stored as a polygon-size value (`3`) followed by three zero-based
  32-bit vertex indices.
- Active Pie/Saw entries store a saw width, plane distance, and approximately
  unit-length normal; the converter normalizes the latter before slicing.
  Advisor applies these cuts to a separate convex planning surface and to a
  hierarchy of previously separated fragments. The plane records alone are
  insufficient to reproduce later Pie/Saw objects.
- The sample rough meshes are closed triangular surfaces satisfying
  `faces = 2 × vertices − 4` and every undirected edge is shared by two faces.

## Compatibility boundary

Every ADV file, including the six supplied samples, follows the same decoder.
The `Output` folder contains reference Advisor exports for comparison, but those
files are not bundled in the app or copied into conversion results. The current
decoder does not reproduce every reference OBJ exactly; `A196-188.adv` currently
fails because a decoded cut does not intersect the rough mesh. A successful
conversion should still be reviewed in MeshLab when exact Advisor geometry matters.

The converter decodes the rough mesh, indexed Galaxy symbols
(`X`, `T`, `V`, `(X)`, `(T)`, `(V)`, and optional `K`), and saw-plane records. Pie
and Saw meshes are cut from the decoded rough surface. Advisor applies later cuts
to a proprietary hierarchy of previously separated fragments; that hierarchy has
not been decoded, so files with later cuts include a visual-review
warning in their conversion details.
Single-plate files may contain no Pie/Saw plan and therefore produce only the
rough OBJ. Galaxy symbol tables vary in record layout and entry count; the
converter validates the declared count when present and fails rather than
silently writing an incomplete CSV. Some variant-2 tables lose two low-order
bytes in a coordinate or shift a record by two bytes; the converter can recover
these records when the entire counted table remains identifiable and flags the
reduced coordinate precision for review.
Some counted tables contain an explicitly inactive, zero-filled symbol slot;
the converter omits that slot and exports every active symbol record.

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
