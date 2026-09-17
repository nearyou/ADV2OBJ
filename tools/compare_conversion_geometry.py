"""Compare generated geometry to independent OBJ exports without requiring identical text."""

import argparse
import json
from pathlib import Path

import numpy as np

from measure_output import mesh, volume


def surface_distances(points, vertices, faces):
    """Exact nearest-triangle distance for each supplied surface probe."""
    triangles = vertices[faces]
    a, b, c = (triangles[:, i] for i in range(3))
    ab, ac = b - a, c - a
    normal = np.cross(ab, ac)
    normal2 = np.einsum("ij,ij->i", normal, normal)
    ab2 = np.einsum("ij,ij->i", ab, ab)
    ac2 = np.einsum("ij,ij->i", ac, ac)
    abac = np.einsum("ij,ij->i", ab, ac)
    denominator = np.maximum(ab2 * ac2 - abac ** 2, 1e-30)
    result = []
    for start in range(0, len(points), 32):
        p = points[start:start + 32, None, :]
        ap = p - a
        dot_ab = np.einsum("ijk,jk->ij", ap, ab)
        dot_ac = np.einsum("ijk,jk->ij", ap, ac)
        u = (ac2 * dot_ab - abac * dot_ac) / denominator
        v = (ab2 * dot_ac - abac * dot_ab) / denominator
        distance2 = np.where((u >= 0) & (v >= 0) & (u + v <= 1) & (normal2 > 1e-20),
                             np.einsum("ijk,jk->ij", ap, normal) ** 2 / np.maximum(normal2, 1e-30), np.inf)
        for x, y in ((a, b), (b, c), (c, a)):
            edge = y - x
            delta = p - x
            t = np.clip(np.einsum("ijk,jk->ij", delta, edge)
                        / np.maximum(np.einsum("ij,ij->i", edge, edge), 1e-30), 0, 1)
            residual = delta - t[:, :, None] * edge
            distance2 = np.minimum(distance2, np.einsum("ijk,ijk->ij", residual, residual))
        result.extend(np.sqrt(distance2.min(axis=1)))
    return np.asarray(result)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reference", type=Path)
    parser.add_argument("generated", type=Path)
    parser.add_argument("--actual-suffix", default="")
    parser.add_argument("--json", type=Path, required=True)
    parser.add_argument("--surface", action="store_true", help="Check cut vertices and triangle centers against the opposite surface in both directions.")
    parser.add_argument("--model", help="Limit comparison to one reference model.")
    args = parser.parse_args()
    results = []
    for reference in sorted(args.reference.glob("*/*.obj")):
        model = reference.parent.name
        if args.model and model != args.model:
            continue
        generated_stem = model + args.actual_suffix
        generated = args.generated / generated_stem / (generated_stem + reference.name[len(model):])
        entry = {"model": model, "object": reference.name, "available": generated.is_file()}
        if generated.is_file():
            expected, expected_faces = mesh(reference)
            actual, actual_faces = mesh(generated)
            entry.update(reference_vertices=len(expected), generated_vertices=len(actual),
                         reference_faces=len(expected_faces), generated_faces=len(actual_faces))
            valid = (len(actual) > 0 and len(actual_faces) > 0 and np.isfinite(actual).all()
                     and actual_faces.min() >= 0 and actual_faces.max() < len(actual))
            entry["valid_coordinates_and_indices"] = bool(valid)
            if valid:
                entry["maximum_bounds_difference"] = float(np.max(np.abs(
                    np.r_[actual.min(0) - expected.min(0), actual.max(0) - expected.max(0)])))
                expected_volume = abs(volume(expected, expected_faces))
                entry["volume_ratio"] = float(abs(volume(actual, actual_faces)) / expected_volume) if expected_volume else None
                same_topology = expected.shape == actual.shape and np.array_equal(expected_faces, actual_faces)
                entry["same_indexed_topology"] = bool(same_topology)
                if args.surface and not reference.name.endswith("_Rough.obj"):
                    forward = surface_distances(np.r_[expected, expected[expected_faces].mean(axis=1)], actual, actual_faces)
                    reverse = surface_distances(np.r_[actual, actual[actual_faces].mean(axis=1)], expected, expected_faces)
                    errors = np.r_[forward, reverse]
                    entry["bidirectional_surface_probes"] = {"count": len(errors), "maximum": float(errors.max()),
                        "rms": float(np.sqrt(np.mean(errors ** 2)))}
                if same_topology:
                    errors = np.linalg.norm(actual - expected, axis=1)
                    entry["vertex_error"] = {"median": float(np.median(errors)),
                                             "rms": float(np.sqrt(np.mean(errors ** 2))),
                                             "maximum": float(errors.max())}
        results.append(entry)
    report = {"reference": str(args.reference), "generated": str(args.generated),
              "note": "Matching bounds or volume alone does not prove equal surfaces.", "objects": results}
    args.json.parent.mkdir(parents=True, exist_ok=True)
    args.json.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    for result in results:
        if result["object"].endswith("_Rough.obj"):
            print(result["model"], result.get("vertex_error", "missing or different indexed topology"))
    print(f"Compared {len(results)} reference objects; {sum(not r['available'] for r in results)} missing.")


if __name__ == "__main__":
    main()
