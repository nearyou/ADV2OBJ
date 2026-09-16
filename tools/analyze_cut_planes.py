"""Relate active ADV saw-plane records to reference fragment meshes."""

from __future__ import annotations

import argparse
import re
import struct
from pathlib import Path

import numpy as np

from compare_reference_meshes import vertices


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("reference_dir", type=Path)
    args = parser.parse_args()
    data = args.adv.read_bytes()
    names = []
    for path in sorted(args.reference_dir.glob("*.obj")):
        match = re.search(r"_(Pie\d+-\d+|Saw\d+-\d+)\.obj$", path.name)
        if not match:
            continue
        name = match.group(1)
        offset = data.find(name.encode("ascii"))
        record_offset = next(
            candidate
            for candidate in range(offset - 72, offset - 47)
            if struct.unpack_from("<d", data, candidate)[0] in (55.0, 70.0, 100.0)
        )
        width, distance, unused, nx, ny, nz = struct.unpack_from("<6d", data, record_offset)
        names.append((name, width, distance, np.array((nx, ny, nz)), path))
        print(f"{name:10} width={width:g} d={distance:10.4f} n={nx: .6f},{ny: .6f},{nz: .6f}")

    print("\nEach mesh: signed-distance range and vertices on every plane")
    for object_name, _width, _distance, _normal, path in names:
        points = np.asarray(vertices(path))
        summaries = []
        for plane_name, _w, distance, normal, _path in names:
            signed = points @ normal - distance
            on = int(np.sum(np.abs(signed) < 0.01))
            if on or plane_name == object_name:
                summaries.append(f"{plane_name}:{signed.min():.1f}..{signed.max():.1f}/on{on}")
        print(object_name, "; ".join(summaries))


if __name__ == "__main__":
    main()
