"""Inspect short label records related to GalaxySymbols export."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    args = parser.parse_args()
    data = args.adv.read_bytes()
    for label in b"XTVK":
        pattern = bytes([label])
        cursor = 0
        records = []
        while True:
            at = data.find(pattern, cursor)
            if at < 0:
                break
            cursor = at + 1
            if at < 4 or struct.unpack_from("<I", data, at - 4)[0] > 32:
                continue
            for delta in (-53, -49, -29, -25, 5, 29):
                try:
                    xyz = struct.unpack_from("<3d", data, at + delta)
                except struct.error:
                    continue
                if all(abs(v) < 10000 for v in xyz):
                    records.append((at, delta, xyz, data[at - 8:at + 8].hex()))
        print(chr(label), len(records))
        for record in records[-30:]:
            print(" ", hex(record[0]), record[1], tuple(round(v, 6) for v in record[2]), record[3])


if __name__ == "__main__":
    main()
