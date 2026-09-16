"""Search valid embedded ADV members for approximate reference OBJ vertices."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from inspect_adv import iter_zip_members


def vertices(path: Path, count: int = 20) -> list[tuple[float, float, float]]:
    result = []
    with path.open("r", encoding="utf-8-sig") as stream:
        for line in stream:
            if line.startswith("v "):
                _, x, y, z = line.split()
                result.append((float(x), float(y), float(z)))
                if len(result) == count:
                    break
    return result


def approximate_hits(payload: bytes, point: tuple[float, float, float], tolerance: float = 0.001):
    suffix = struct.pack("<d", point[0])[4:]
    cursor = 0
    while True:
        suffix_at = payload.find(suffix, cursor)
        if suffix_at < 0:
            return
        offset = suffix_at - 4
        cursor = suffix_at + 1
        if offset < 0 or offset + 24 > len(payload):
            continue
        candidate = struct.unpack_from("<ddd", payload, offset)
        if all(abs(a - b) <= tolerance for a, b in zip(candidate, point)):
            yield offset, candidate


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("obj", type=Path)
    args = parser.parse_args()
    probes = vertices(args.obj)
    raw = args.adv.read_bytes()
    found = 0
    for member_index, (base, name, compressed, size, payload, valid) in enumerate(iter_zip_members(raw)):
        matches = []
        for vertex_index, point in enumerate(probes):
            hits = list(approximate_hits(payload, point))
            if hits:
                matches.append((vertex_index, hits[:3]))
        if matches:
            found += 1
            print(
                f"member {member_index} base={base:#x} name={name!r} "
                f"compressed={compressed} size={size} valid={valid} matches={matches}"
            )
    print(f"members with reference vertices: {found}")


if __name__ == "__main__":
    main()
