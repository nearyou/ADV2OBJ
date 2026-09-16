"""Find candidate Galaxy symbol headers in ADV files."""

from __future__ import annotations

import argparse
import csv
import math
import struct
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("csv", type=Path)
    args = parser.parse_args()
    data = args.adv.read_bytes()
    start = max(0, len(data) - 4_000_000)
    candidates = []
    for offset in range(start, len(data) - 40):
        code, ordinal, one, zero = struct.unpack_from("<4I", data, offset)
        if code > 12 or ordinal > 20 or one not in (0, 1) or zero != 0:
            continue
        xyz = struct.unpack_from("<3d", data, offset + 16)
        if (sum(value * value for value in xyz) > 10_000
                and all(math.isfinite(value) and abs(value) < 10_000 for value in xyz)):
            candidates.append((offset, code, ordinal, xyz))
    print("candidates", len(candidates))
    for row in candidates:
        print(hex(row[0]), row[1], row[2], tuple(round(v, 5) for v in row[3]))
    print("reference")
    with args.csv.open(newline="", encoding="utf-8-sig") as stream:
        for row in csv.reader(stream):
            print(row)


if __name__ == "__main__":
    main()
