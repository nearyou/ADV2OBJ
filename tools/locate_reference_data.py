"""Locate reference OBJ/CSV values in raw and embedded ADV records."""

from __future__ import annotations

import argparse
import re
import struct
from pathlib import Path

from inspect_adv import iter_zip_members


def first_vertices(path: Path, count: int = 3) -> list[tuple[float, float, float]]:
    vertices = []
    with path.open("r", encoding="utf-8-sig") as stream:
        for line in stream:
            if line.startswith("v "):
                _, x, y, z = line.split()
                vertices.append((float(x), float(y), float(z)))
                if len(vertices) == count:
                    break
    return vertices


def offsets(blob: bytes, needle: bytes, maximum: int = 6) -> list[int]:
    found = []
    cursor = 0
    while len(found) < maximum:
        cursor = blob.find(needle, cursor)
        if cursor < 0:
            break
        found.append(cursor)
        cursor += 1
    return found


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("reference_dir", type=Path)
    args = parser.parse_args()

    raw = args.adv.read_bytes()
    members = list(iter_zip_members(raw))
    print(f"raw={len(raw):,}; decompressible members={len(members)}")

    plan_match = next(
        (re.search(r"Pie(\d+)-", p.stem) for p in args.reference_dir.glob("*Pie*.obj")),
        None,
    )
    plan_number = plan_match.group(1) if plan_match else ""
    for term in filter(None, ("GalaxySymbols", "SawsMD", f"Pie{plan_number}", f"Saw{plan_number}")):
        needle = term.encode("ascii")
        print(f"term {term!r}: raw={[hex(x) for x in offsets(raw, needle, 20)]}")
        for index, (base, name, _csize, _size, payload, _valid) in enumerate(members):
            hits = offsets(payload, needle, 20)
            if hits:
                print(f"  zip[{index}]@{base:#x} {name!r}: {[hex(x) for x in hits]}")

    for obj in sorted(args.reference_dir.glob("*.obj")):
        vertices = first_vertices(obj)
        print(f"\n{obj.name}: {vertices[0]}")
        for fmt, label in (("<fff", "f32 triple"), ("<ddd", "f64 triple")):
            needle = struct.pack(fmt, *vertices[0])
            hits = offsets(raw, needle)
            if hits:
                print(f"  raw {label}: {[hex(x) for x in hits]}")
            for index, (base, name, _csize, _size, payload, _valid) in enumerate(members):
                hits = offsets(payload, needle)
                if hits:
                    print(f"  zip[{index}]@{base:#x} {name!r} {label}: {[hex(x) for x in hits]}")

        # Values written by MeshInspector may be rounded. Search each coordinate
        # independently as float32 to identify candidate records.
        for axis, value in zip("xyz", vertices[0]):
            needle = struct.pack("<f", value)
            raw_hits = offsets(raw, needle)
            member_hits = []
            for index, (base, name, _csize, _size, payload, _valid) in enumerate(members):
                hits = offsets(payload, needle)
                if hits:
                    member_hits.append((index, base, name, hits))
            if raw_hits or member_hits:
                print(f"  {axis} f32 {value}: raw={[hex(x) for x in raw_hits]}; members={member_hits[:8]}")


if __name__ == "__main__":
    main()
