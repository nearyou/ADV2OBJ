"""Search ADV data for rounded reference OBJ vertex triples."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np

from inspect_adv import iter_zip_members
from locate_reference_data import first_vertices


def approximate_triples(blob: bytes, target: tuple[float, float, float], dtype: str, tolerance: float):
    item_size = np.dtype(dtype).itemsize
    wanted = np.asarray(target, dtype=np.float64)
    results = []
    for alignment in range(item_size):
        usable = (len(blob) - alignment) // item_size * item_size
        if usable < item_size * 3:
            continue
        values = np.frombuffer(blob, dtype=dtype, count=usable // item_size, offset=alignment)
        candidates = np.flatnonzero(np.abs(values[:-2].astype(np.float64) - wanted[0]) <= tolerance)
        for index in candidates:
            triple = values[index:index + 3].astype(np.float64)
            if np.all(np.abs(triple - wanted) <= tolerance):
                results.append(alignment + int(index) * item_size)
                if len(results) == 50:
                    return results
    return results


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("reference_dir", type=Path)
    args = parser.parse_args()
    raw = args.adv.read_bytes()
    members = list(iter_zip_members(raw))
    print(f"members={len(members)}, payload bytes={sum(len(m[4]) for m in members):,}")
    print("largest:")
    for index, member in sorted(enumerate(members), key=lambda p: len(p[1][4]), reverse=True)[:20]:
        print(index, hex(member[0]), len(member[4]))

    for obj in sorted(args.reference_dir.glob("*.obj")):
        target = first_vertices(obj, 1)[0]
        print(f"\n{obj.name} {target}")
        for dtype, tolerance in (("<f4", 0.001), ("<f8", 0.001)):
            hits = approximate_triples(raw, target, dtype, tolerance)
            if hits:
                print(dtype, "raw", [hex(x) for x in hits])
            for index, member in enumerate(members):
                hits = approximate_triples(member[4], target, dtype, tolerance)
                if hits:
                    print(dtype, f"member {index}@{member[0]:#x}", [hex(x) for x in hits])


if __name__ == "__main__":
    main()
