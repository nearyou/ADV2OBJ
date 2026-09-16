"""Show binary framing around candidate GalaxySymbols coordinate triples."""

from __future__ import annotations

import argparse
import csv
import struct
from pathlib import Path

from scan_mesh_values import approximate_triples


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("csv", type=Path)
    args = parser.parse_args()
    data = args.adv.read_bytes()
    with args.csv.open(newline="", encoding="utf-8-sig") as stream:
        rows = list(csv.reader(stream))
    for row in rows:
        target = tuple(float(value) for value in row[1:4])
        print("\n", row[0], target)
        for hit in approximate_triples(data, target, "<f8", 0.001):
            ints_before = struct.unpack_from("<4I", data, hit - 16)
            ints_after = struct.unpack_from("<4I", data, hit + 24)
            previous = struct.unpack_from("<3d", data, hit - 24)
            following = struct.unpack_from("<3d", data, hit + 24)
            print(
                hex(hit),
                "ib", ints_before,
                "ia", ints_after,
                "prev", tuple(round(v, 2) for v in previous),
                "next", tuple(round(v, 2) for v in following),
            )


if __name__ == "__main__":
    main()
