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
Counted Galaxy tables are also accepted when the count follows the table header
at the alternate offsets used by the supplied files. A variant-1 symbol record
with a two-byte header loss is recovered only when its remaining header, table
code, ordinal, and coordinates all validate; the repair is reported for review.

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
An earlier run of the 177-file `adv-in` regression corpus converted 172 files;
that historical result does not establish exact Advisor export equivalence or
universal ADV support.

In the 13-file `fail` set supplied on September 21, seven now complete through
the normal data-driven pipeline. Two partially damaged rough meshes recover from
their declared vertex/face counts and surviving low index bytes. Three additional
Galaxy table layouts are decoded. A stored cut lying wholly outside the rough
mesh is treated as inactive and omitted with a warning, and a saw tree with a
missing intermediate branch applies its remaining recorded ancestors and reports
the missing branch. The resulting 65 OBJ files pass finite-coordinate, index,
and closed-edge validation. A subsequent recovery improvement also converts
`27_8_396.83A-A2.2-1291-7-1.adv`: its uncompressed mesh contains multiple byte
deletions. The fallback anchors coordinate alignment to the declared record
length and the face table, preserving vertex numbering across the shifts. It
retains all 8,515 vertices and 17,022 source triangles, reconstructs four missing
triangles, and repairs 112 vertices. The resulting ten OBJ files and CSV/INI
companions require visual review; exact Advisor equivalence has not been verified.
This recovery requires at least 99% readable source triangles, consistent directed
edge connectivity, small simple boundary loops, and closed final topology.
It never uses input names or sample-output substitutions.

The five remaining dense-mesh failures now export through a polygon-surface
fallback. It reads the paired polygon records preceding the dense scan record
and requires the duplicate coordinate blocks to agree. Record lengths, polygon
counts, surviving polygon planes, retained points and closed topology validate
the recovered surface. Invalid coordinates can be repaired from incident source
planes or a uniquely supported high-byte correction. A repeated cap sign error
is corrected only when it improves point retention and source-plane agreement.

These are **coarser source surfaces**, not exact dense-scan exports. The UI marks
them `Review (coarse)` and explains the recovery in Details. An optional refinement
now uses the validated polygon surface to guide byte alignment of the dense scan.
It retains at least 95% of the scan points, checks readable source connectivity,
requires at least 85% agreement with those readable triangles and a closed surface,
and limits volume change to 5%. Readable connectivity must cover at least 20% of
the expected surface to provide an independent check. Reconstructed triangles,
repaired coordinates and omitted points are reported as `Review (scan)`. If these
checks fail, or the refined surface cannot produce every recorded cut, conversion
retains the polygon fallback. Existing successful
decoders retain priority. No sample names, hashes, or reference-output lookups are
used. The current seven-file `Downloads/input` batch produces 59 OBJ files plus
seven CSV/INI pairs, with no conversion failures. All 59 OBJs pass finite-value,
index and closed-edge validation. The six original samples still produce the
same 67 output files byte-for-byte as the preceding build.

| Recovered input | OBJ files | Exported rough vertices |
| --- | ---: | ---: |
| `1_9_457.62C-A2.1-726-7-1.adv` | 10 | 614 |
| `18-8-AU3-396.52-14++57-1.adv` | 7 | 462 |
| `31_8_457.62C-A2.1-4-8-1.adv` | 8 | 919 |
| `31_8_457.62C-A2.1-7.adv` | 9 | 754 |
| `31_8_457.62C-A2.1-8-8-1.adv` | 11 | 10,262 (scan refinement; previously 608) |

The refined input retains 10,262 of 10,271 scan points, with 99 repaired
coordinates and nine uncertain points omitted. Its reconstructed surface preserves
5,487 of 6,126 readable source triangles; its volume is 1.67% below the coarse
surface. Those checks support using the denser data but do not establish exact
Advisor equivalence. The other six current inputs, all CSV/INI companions, and
all six original samples remain byte-for-byte unchanged from the polygon build.
Independent synthetic checks verify intact-coordinate preservation and rejection
of inconsistent source connectivity. Input files were retained during testing.

The later `31-8-AU9-477.19-345++16-1.adv` input stores its five counted
Galaxy symbols in code order `1, 2, 3, 4, 0`. Counted tables are now bound by
their stored code and ordinal without requiring code zero to be physically first.
It completes with four OBJ files plus its CSV and INI companions.

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

## September 22 container and scan recovery

All 30 files in the supplied `Downloads/New folder` batch convert to 107 OBJ
files and 30 CSV/INI pairs. All 107 meshes pass finite-coordinate, index-range,
closed-edge and surface checks. The six original samples (67 files) and seven
previous inputs (73 files) remain byte-identical to the preceding release.
The tested single-file build is `Release/2026-09-22/ADV2OBJ.exe`.

The `Downloads/New folder` inputs exposed additional formats. Cutting collections
can appear tens of megabytes after the rough record. When the normal cache window
contains no plan, the decoder searches typed cut records throughout the container.
It exports the first readable stored plan and explicitly asks the user to verify
plan selection; a missing cache does not establish which historical plan was active.
Signed `-1` root-piece identifiers are supported. Invalid source-piece references
are rejected while looking for another complete collection. If incomplete root
ancestry erases a cut entirely, its stored slab is retained with a review warning.

A counted scan recovery handles small gaps in triangle records, including gaps
sharing a vertex. It retains at least 99% readable source triangles, opens only
small local patches, restores any interior scan vertices without moving their
coordinates, and requires the declared face count and closed edge connectivity.
Duplicate or conflicting connectivity is rejected. Shifted coordinate records can
use an independently validated polygon surface as an alignment guide. This path
also supports the 55,098-vertex input. Previously successful decoders retain
priority for shifted scans. Intact typed mesh counts take precedence over assuming
that every closed surface satisfies `F = 2V - 4`.

Bounded Galaxy tables with repeated internal codes are accepted when every label
ordinal is distinct and both stored copies of each coordinate triple match.
The exported coordinates remain unchanged; missing ordinals are rejected.

Synthetic tests cover touching triangle gaps, unchanged coordinates, conflicting
triangles, non-spherical topology, distant root cuts, incomplete root ancestry,
and duplicate-code symbol tables. These recoveries use input records, never sample
filenames, hashes, or reference-output substitutions. Review warnings remain
necessary; conversion success does not prove exact Advisor equivalence.

## September 22 stopped-file batch

The 14 inputs in `Downloads/stopped file` now produce 123 OBJ files and 14
CSV/INI pairs. All 123 meshes pass finite-coordinate, index, closed-edge and
surface checks. The 30-file preceding batch, seven earlier inputs, and six
original samples retain byte-identical outputs (307 files). The single-file
Windows build is `Release/2026-09-22-stopped/ADV2OBJ.exe`.

Galaxy tables are located by their typed record signature even when more than
eight megabytes precede the end of the container. Small triangle-table gaps can
be repaired without reusing occupied diagonals; a fallback removes dangling
false-marker triangles and triangulates bounded nonplanar gaps. Declared counts
and closed connectivity must still agree.

When the dense scan cannot be recovered reliably, adjacent typed records can
independently bound a stored polygon surface despite damaged length words.
Coordinate repairs require corroborating incident planes; a repeated corrupt
polygon index is repaired only when at least three incident polygons uniquely
identify the same stored replacement vertex. Surface-point retention and source
plane validation remain required. New shifted-gap recoveries that produce a
sustained population of implausibly long edges use the verified polygon surface
instead of exporting folded scan geometry.

Five inputs in this batch use this explicitly marked coarse recovery:
`1429.28-1772`, `1429.28-1853`, `1429.28-1862`, `1429.28-1922`, and
`1429.28-1954`. Their outputs are approximations requiring MeshLab review, not
verified exact Advisor exports. Two other inputs retain their scan coordinates
with five missing triangles reconstructed each. Existing cut-plan review
warnings remain, including the unavailable ancestor branch in `601-72`.
No filename/hash matching or sample-output substitution is used in production.
All 14 source files were retained during validation.

## September 22 replacement New folder batch

The replacement `Downloads/New folder` contains 28 inputs. All 28 convert to
122 OBJ files, 28 CSV files, and 28 INI files. All 122 meshes pass finite-value,
index, closed-edge, and surface checks. Regression conversion of the previous
30-file batch, 14 stopped files, seven earlier inputs, and six original samples
preserves all 458 output files byte for byte. The single-file Windows build is
`Release/2026-09-22-batch28/ADV2OBJ.exe`. Validation retained all source inputs.

Symbol decoding now handles additional stored mode values, shortened reserved
headers, and short first coordinate copies. Recovery uses the complete duplicate
coordinate block only when the surviving first-copy bytes agree. Explicit empty
symbol slots support both count locations and the additional inactive mode.
Unknown slots are not populated with invented coordinates.

Generic topology repair rejects coordinate boundaries that conflict with a typed
scan record before running geometry repair. A cancellation callback also allows
cooperative repair cancellation. Stored polygon recovery can use independently
bounded surfaces when dense counts are absent or large, and repair a reflected
cap only when source-plane agreement and surface-point retention validate it.

For a broken compressed stream, a bounded search can restart at a later dynamic
DEFLATE block. Two synthetic dictionary histories identify every output byte
that depends on unavailable history. Such coordinate bytes are marked unknown
before incident-plane recovery; dependent type signatures and count bytes are
rejected. Only the validated coarse polygon surface is exported from this path.

Six inputs use coarse recovery: `1429.28-1204`, `1429.28-2242`,
`1429.28-2313`, `1429.28-2643`, `1429.28-2683`, and `1429.28-318`. These require MeshLab review
and are not verified exact Advisor exports. Existing cut-history, alternate-mesh,
coordinate-repair, and approximate-cap warnings remain visible. Synthetic tests
cover the new symbol layouts, exact copied coordinates, inactive slots, restart
after a broken compressed block, and exclusion of dictionary-dependent data.

## September 22 accuracy update

All 85 available regression inputs still convert, producing 467 OBJ files and
85 CSV/INI pairs. The single-file build is
`Release/2026-09-22-accuracy/ADV2OBJ.exe`. Validation retains source inputs.

After a compressed-stream restart, the converter now preserves a complete dense
scan when every coordinate and triangle byte is independent of the missing
dictionary. Record boundaries, closed topology, bounds, volume, and containment
within the independently recovered polygon guide must agree. For `1429.28-2242`,
this preserves all 10,663 measured vertices and 21,322 source triangles instead
of exporting a 134-vertex approximation. Direct comparison confirms the rough
OBJ coordinates and triangle indices match the decoded source arrays exactly.
Synthetic checks reject this recovery if any geometry byte depends on missing
history.

A verified primary stored surface takes precedence over a later cached raw mesh.
This restores the missing Saw17-1 output in `1429.28-1237`. If the recovered
surface cannot produce a valid cutting plan, the established fallback remains
available. Distortion checks now apply to every shifted declared scan recovery;
`1429.28-155` and `1429.28-197` use verified stored polygon surfaces instead of
scans with many implausibly long edges. These are explicitly marked coarse
recoveries requiring visual review.

Only these four models change in the regression set. All CSV/INI files and the
six original sample conversions remain byte-identical to the preceding release.
The changes use input records and geometry validation, with no filename/hash
matching or reference-output substitution in production. Structural validation
does not establish exact equivalence to Advisor exports; reconstructed cuts and
coarse recoveries retain their review warnings.

## September 25 failed-file batch

The 66 files supplied in `Downloads/fail (2)/fail` exposed additional damaged or
unrecognized mesh encodings. The updated pipeline converts 51 files to 158 OBJ
files and 51 CSV/INI pairs; 15 files still fail. This does not establish exact
Advisor equivalence. The new recoveries export the stored polygon approximation
and explicitly require visual review. The single-file Windows build is
`Release/2026-09-25-recovery/ADV2OBJ.exe`.

Upper index bytes are resolved against geometric supporting planes, including
records with more than 256 vertices. Acceptance requires retaining every stored
surface vertex, corroborating at least 95% of the declared planes and 90% of the
polygon index slots, and passing closed-surface validation. Repeated cap heights
can be recovered from coplanarity only when several source polygons agree on a
unique height. Unreadable polygon sizes can use the remaining bounded records.

A later stored rough copy is eligible only when its vertex count agrees and at
least 90% of its complete XYZ byte triples occur in the bounded primary polygon
record. An unrelated cached plan mesh is rejected. Complete polygon records in a
readable DEFLATE prefix can be recovered even when a later part of the compressed
member fails. Inactive Galaxy tables prefer matching duplicated coordinates over
header-like values inside nested records.

All 85 previous regression inputs continue converting with byte-identical output
(637 files). Synthetic tests cover masked indices above 255, unchanged measured
coordinates, cap heights, damaged size words, unrelated cached meshes, and
inactive-symbol decoys. All 158 new meshes pass coordinate, index, closed-edge,
and surface checks. Validation retains all source ADV files. No filename/hash
lookups or reference-output substitution are used in these recovery paths.

The remaining failures are listed in `artifacts/fail66-verified.txt`. Those files
still need a decoder that can establish their missing geometry; they are not
reported as successful conversions.

## September 25 contour recovery update

The later build, `Release/2026-09-25-contours/ADV2OBJ.exe`, converts all 66 inputs
in that batch, including the 15 failures above. Combined output is 221 OBJ files
and 66 CSV/INI pairs. All 85 older regression inputs also pass. The 897 output
files from the 136 previously successful inputs are byte-identical to the
preceding build. Test conversions retain the source ADV files.

This last-resort decoder reads the primary rough record's counted horizontal
XY contours. It validates height spacing, record boundaries, coordinate ranges,
outline intersections, terminal coverage, and closed surface topology. It keeps
the measured concavities and uses an ordered shortest-strip triangulation to
connect adjacent contours without moving their measured points. A later stored
contour copy can fill gaps only after broad agreement with the primary record's
exact coordinates, height spacing, and XY extents. Large unsupported gaps and
unrelated stored models remain rejected.

The recovered files are labelled **Review (contours)**. Their warnings disclose
the recovered contour count, omitted unreadable points, bridged levels, and
largest interval. Their OBJ headers also identify reconstruction. These are
usable reconstructed surfaces, not certified exact Advisor exports. The 63 new
OBJ files pass coordinate, index, closed-edge, and surface checks. Synthetic
tests verify measured coordinates and concavities, masked headers, bounded gaps,
complete terminal coverage, copy corroboration, rejection of unrelated models,
and cancellation. No filename/hash lookup or reference-output substitution is
used. The checked-by-default deletion preference remains unchanged.

See `artifacts/contour-recovery-report-2026-09-25.md` for results and limitations.
Success on the available inputs does not establish support for every possible
ADV version or file with insufficient readable geometry.

## September 25 measured-detail update

`Release/2026-09-25-precision/ADV2OBJ.exe` retains an additional 41,061 measured
points and 253 contour levels across the 15 contour-recovered inputs. All 151
available inputs convert (66 recent files and 85 earlier regressions). The 897
output files from the other 136 inputs remain byte-identical, as do all CSV/INI
files for the refined models. All 63 refined OBJ meshes pass structural checks.

Neighboring counted records now help recover a contour header with one damaged
height byte. When all height bytes survive, a verified record boundary can also
recover damaged version framing. Conflicting candidates remain rejected.
For small local byte gaps, the decoder reads intact coordinates independently
from both ends, checks them against the adjacent measured contours, and retains
only points common to every supported alignment. It never interpolates missing
XY coordinates. The established recovery remains available if the additional
measurements cannot produce a valid surface or cutting plan.

Synthetic tests cover exact coordinate preservation across several byte-gap
alignments, damaged height/version fields, conflicting counts, unsupported gaps,
and the existing rejection and cancellation behavior. Source inputs were retained
during regression tests. See `artifacts/precision-report-2026-09-25.md` for the
per-model comparison. These changes reduce reconstruction; they do not certify
exact equivalence to Advisor exports or every unknown ADV format.

## September 29 spiked-mesh correction

`Release/2026-09-29-mesh-fix/ADV2OBJ.exe` corrects the four models supplied in
`Downloads/adv+obj damag`. The previous executable's OBJ output was reproduced
byte for byte before applying the fix. Closed triangle connectivity had hidden
incorrectly aligned coordinates, producing very long spikes. A separate case
produced a cut with unmatched edges even though its rough surface looked intact.

Recovered dense scans now undergo a geometry check using triangle edge lengths
and robust coordinate extents. When that check detects a sustained population
of spikes, the converter first tries the counted dense record aligned against
an independently validated stored surface, then the validated polygon surface.
If a dense scan produces a non-manifold cut and a stored polygon recovery is
available, the entire output set is retried against that surface. Rough and
Saw/Pie files therefore use the same replacement geometry.

The corrected `1085.48-347` keeps its 12,195-vertex dense scan. `1085.48-703`,
`1085.48-715`, and `1085.48-985` use stored polygon surfaces and retain the
**Review (coarse)** label. Their detail is lower than a valid dense scan; exact
equivalence to Advisor exports has not been established. All eight resulting
OBJ meshes pass finite-coordinate, index, closed-edge and surface checks, and
all four CSV/INI pairs are unchanged. No input-name/hash shortcuts are used.

Validation passed on all 155 available inputs: the four reported files plus
all 151 earlier inputs. One earlier model (`27_8_396.83A-A2.2-1291-7-1`) also
triggered the new geometry/cut checks and now uses its validated stored polygon
surface; its ten OBJ files change. The other 980 previous output files are
byte-identical, including every previous CSV/INI file. All 59 OBJ files in the
seven-input regression group containing that model pass structural checks.
See `artifacts/damage-report-2026-09-29.md` for evidence and accuracy limits.
