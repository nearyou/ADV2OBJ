"""Compare reference planning meshes with the rough mesh and one another."""

from __future__ import annotations

import argparse
import math
from pathlib import Path


def vertices(path: Path) -> list[tuple[float, float, float]]:
    result = []
    with path.open("r", encoding="utf-8-sig") as stream:
        for line in stream:
            if line.startswith("v "):
                _, x, y, z = line.split()
                result.append((float(x), float(y), float(z)))
    return result


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("reference_dir", type=Path)
    args = parser.parse_args()
    rough_path = next(args.reference_dir.glob("*_Rough.obj"))
    rough = vertices(rough_path)
    rough_set = {(round(x, 3), round(y, 3), round(z, 3)) for x, y, z in rough}
    for path in sorted(args.reference_dir.glob("*.obj")):
        if path == rough_path:
            continue
        verts = vertices(path)
        exactish = sum((round(x, 3), round(y, 3), round(z, 3)) in rough_set for x, y, z in verts)
        sample = verts[: min(20, len(verts))]
        distances = []
        for point in sample:
            distances.append(min(math.dist(point, other) for other in rough))
        print(
            f"{path.name}: vertices={len(verts)}, rough matches={exactish}, "
            f"sample nearest min/mean/max={min(distances):.5g}/{sum(distances)/len(distances):.5g}/{max(distances):.5g}"
        )


if __name__ == "__main__":
    main()
