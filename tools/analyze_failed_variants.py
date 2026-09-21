"""Read-only structural report for failed ADV variants."""

from pathlib import Path
import math
import struct
import sys


GALAXY = bytes.fromhex("F36BED43B332FA607EE3551D")


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def f64(data, offset):
    return struct.unpack_from("<d", data, offset)[0]


for path in sorted(Path(sys.argv[1]).glob("*.adv")):
    data = path.read_bytes()
    positions = []
    cursor = 0
    while True:
        cursor = data.find(GALAXY, cursor)
        if cursor < 0:
            break
        positions.append(cursor)
        cursor += 1
    print("\n", path.name, "galaxy headers", len(positions))
    for header in positions[-8:]:
        if header + 32 > len(data):
            continue
        count = u32(data, header + 28)
        print(" header", hex(header), "count", count,
              "next", data[header + 32:header + 128].hex(" "))
        # Report plausible double triples and their preceding words near the table.
        hits = []
        for offset in range(header + 32, min(len(data) - 24, header + 2000)):
            xyz = (f64(data, offset), f64(data, offset + 8), f64(data, offset + 16))
            if all(math.isfinite(value) and abs(value) < 100_000 for value in xyz) \
                    and sum(value * value for value in xyz) > 10_000:
                hits.append((offset, xyz))
        for offset, xyz in hits[:12]:
            before = tuple(u32(data, offset - delta) for delta in (16, 12, 8, 4))
            print("  xyz", hex(offset), tuple(round(value, 4) for value in xyz),
                  "before", before)
