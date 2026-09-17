# ADV2OBJ

Windows batch converter for extracting Sarine Advisor `.adv` geometry into a
per-stone directory of Wavefront `.obj` files plus companion `.csv` and `.ini`
files.

## User workflow

1. Select the input directory containing `.adv` files.
2. Select the output directory.
3. **Delete after conversion** is checked by default to remove successfully converted inputs. Uncheck it to keep them.
4. Click **Convert**.
5. Review per-file completion, repair, or failure details in the status table.

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
When **Delete after conversion** is checked, the app deletes the source `.adv`
after a successful publish. The choice applies to the whole batch and cannot be
changed while it runs. Failed or cancelled inputs remain in place. If deletion
is selected but the source changed during conversion or
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

The release is `Release/ADV2OBJ.exe`. Copy that file alone to a
64-bit Windows computer; it includes the .NET 8 Desktop Runtime and the app's
resources. Advisor and MeshLab are not required to run the converter.

## Sample validation

```powershell
dotnet run --project tests/Adv2Obj.Validation/Adv2Obj.Validation.csproj -- .
```

This compares generated files against the supplied Advisor exports and exits
with a failure status for differences or conversion errors. It reports each
sample separately so decoder gaps remain visible.

Run independent, synthetic decoder checks with:

```powershell
dotnet run --project tests/Adv2Obj.Validation/Adv2Obj.Validation.csproj -- --decoder-check
```

These checks cover serialized mesh boundaries, cut-plane equations, Pie numbering
from zero or one, declared Galaxy-symbol tables, touching-surface topology, and
identical output when an input is renamed. They also check source-piece selection,
both branches of a saw tree, concave cut caps, and preservation of measured points
when a triangle table is incomplete. They use
generated ADV records, not stored sample exports. `tools/compare_conversion_geometry.py`
also reports bounds, volume, and same-topology coordinate errors against
reference OBJ files; equal bounds or volume alone do not establish equal shapes.
Its `--surface` option additionally measures nearest-triangle distances from every
cut vertex and triangle center in both directions, independent of triangulation.

The supplied paired exports establish these format facts:

- ADV uses the 16-byte signature `CA 9B 28 C7 D7 EC 9B 45 AA BF E4 42 3F EF 0E FF`.
- The primary rough-mesh record is an embedded raw-DEFLATE ZIP member named
  `ZippedData`.
- Coordinates are stored as little-endian 64-bit floating-point triples.
- Faces are stored as a polygon-size value (`3`) followed by three zero-based
  32-bit vertex indices.
- Active Pie/Saw entries store a saw width, plane distance, and approximately
  unit-length normal; the converter preserves the stored plane coefficients
  when slicing, so rescaling the normal cannot move the cut.
  Serialized cut collections describe the separated Pie pieces and the binary
  saw tree within each piece. Later cuts exclude previously removed material;
  saw cuts use the piece and ancestor sides stored in these collections.
- The sample rough meshes are closed triangular surfaces satisfying
  `faces = 2 × vertices − 4` and every undirected edge is shared by two faces.
- The declared mesh-record byte length identifies the vertex-table boundary,
  including in supported records with missing bytes. This takes precedence over
  searching for bytes that happen to decode to plausible coordinates.
- Explicit Galaxy-table counts take precedence over inferred symbol sequences;
  unrelated records are not added to an explicitly counted table.
- Pie companion pairing detects whether that plan numbers its records from zero
  or one. This avoids pairing `Pie1-0` with a nonexistent negative ordinal.
- If clipping welds separate surface fans onto a shared edge, their vertex
  indices are separated. This preserves every triangle's coordinates and winding;
  the result is used only when every resulting edge has exactly two faces.

## Compatibility boundary

Every ADV file, including the six supplied samples, follows the same decoder.
The `Output` folder contains reference Advisor exports for comparison, but those
files are not bundled in the app or copied into conversion results. The current
decoder does not reproduce every reference OBJ exactly. All six original samples
produce complete folders in the September 18 cut-history build. `A196-188.adv`
requires reconstruction of part of its triangle table; its coordinates are
preserved, but its recovered surface is approximate. A successful conversion
should still be reviewed in MeshLab when exact Advisor geometry matters.

The converter decodes the rough mesh, indexed Galaxy symbols
(`X`, `T`, `V`, `(X)`, `(T)`, `(V)`, and optional `K`), and saw-plane records. Pie
and Saw meshes are cut from the decoded rough surface using the decoded cut
collections when available. Collection records determine Pie pairing, separated
piece ownership, and positive/negative branches of earlier saw cuts. Concave
caps are triangulated along their boundaries, rather than filled with a center fan.
If a boundary on an already reconstructed rough surface cannot be triangulated,
the older approximate cap fallback remains available and adds an explicit warning.
Unrecognized collection layouts still use independent plane reconstruction and
require visual review.
Single-plate files may contain no Pie/Saw plan and therefore produce only the
rough OBJ. Galaxy symbol tables vary in record layout and entry count; the
converter validates the declared count when present and fails rather than
silently writing an incomplete CSV. Some variant-2 tables lose two low-order
bytes in a coordinate or shift a record by two bytes; the converter can recover
these records when the entire counted table remains identifiable and flags the
reduced coordinate precision for review.
Some counted tables contain an explicitly inactive, zero-filled symbol slot;
the converter omits that slot and exports every active symbol record.

Additional inputs have different record layouts or records the decoder cannot
fully interpret. The decoder can
recover a DEFLATE member when the local ZIP method bytes are missing and can
decode a raw, uncompressed rough-mesh record when no ZIP member is present. If
the primary triangle table is incomplete but its counted vertex block is intact,
it can recover a radial surface from those measured points, restoring surviving
source diagonals. This recovery requires every input point to be retained and at
least 80% of the resulting triangles to match surviving source records. The
remaining triangles are explicitly reported as reconstructed. Other topology
repairs and alternate embedded meshes remain fallbacks and carry review warnings.
In the 177-file `adv-in` regression corpus, 172 convert and five still fail; this
does not establish exact Advisor export equivalence or universal ADV support.

The six additional files described as Advisor 7.6 in `D:\ADV2OBJ\For-obj\76-new` also
produce complete folders. Two files lose bytes within a vertex record; the
decoder preserves the surviving vertices on both sides and reconstructs the
affected coordinates. When a Pie companion boundary would erase an entire
slice, the converter exports the cut-plane slab and marks it for review. These
meshes are approximations. Touching surface fans are separated without changing
triangle coordinates; any remaining non-manifold cut edges are identified in the
conversion details for MeshLab review.

Because ADV is proprietary and the samples show multiple damaged/variant record
layouts, broader production compatibility requires more samples from every
Advisor version and scanner workflow that must be supported.
