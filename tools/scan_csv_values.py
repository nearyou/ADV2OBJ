"""Search ADV data for rounded GalaxySymbols coordinate triples."""

from __future__ import annotations

import argparse
import csv
from pathlib import Path

from inspect_adv import iter_zip_members
from scan_mesh_values import approximate_triples


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("csv", type=Path)
    args = parser.parse_args()
    raw = args.adv.read_bytes()
    members = list(iter_zip_members(raw))
    with args.csv.open(newline="", encoding="utf-8-sig") as stream:
        rows = list(csv.reader(stream))
    for row in rows:
        target = tuple(float(value) for value in row[1:4])
        print(row[0], target)
        for dtype, tolerance in (("<f4", 0.001), ("<f8", 0.001)):
            hits = approximate_triples(raw, target, dtype, tolerance)
            if hits:
                print(" ", dtype, "raw", [hex(x) for x in hits])
                for hit in hits:
                    nearby = raw[max(0, hit - 100):hit + 124]
                    labels = []
                    for label in (b"X", b"T", b"V", b"(X)", b"(T)", b"(V)"):
                        cursor = 0
                        while True:
                            cursor = nearby.find(label, cursor)
                            if cursor < 0:
                                break
                            labels.append((label.decode(), cursor - min(100, hit)))
                            cursor += 1
                    if labels:
                        print("    nearby", hex(hit), labels)
            for index, member in enumerate(members):
                hits = approximate_triples(member[4], target, dtype, tolerance)
                if hits:
                    print(" ", dtype, f"member {index}@{member[0]:#x}", [hex(x) for x in hits])


if __name__ == "__main__":
    main()
