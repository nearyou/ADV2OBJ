"""Rank one-bit ADV DEFLATE repairs by recovered mesh edge consistency."""

from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path

from analyze_repair_candidate import MARKERS, decode


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("first", type=lambda value: int(value, 0))
    parser.add_argument("last", type=lambda value: int(value, 0))
    parser.add_argument("vertices", type=int)
    args = parser.parse_args()
    raw = args.adv.read_bytes()
    header = raw.find(b"PK\x03\x04")
    compressed_size = struct.unpack_from("<I", raw, header + 18)[0]
    name_len, extra_len = struct.unpack_from("<HH", raw, header + 26)
    start = header + 30 + name_len + extra_len
    compressed = bytearray(raw[start:start + compressed_size])
    rankings = []
    for position in range(args.first, args.last + 1):
        original = compressed[position]
        for bit in range(8):
            compressed[position] = original ^ (1 << bit)
            try:
                payload = zlib.decompress(compressed, -15)
            except zlib.error:
                continue
            count = 0
            for offset in range(len(payload) - 16, -1, -16):
                if struct.unpack_from("<I", payload, offset)[0] not in MARKERS:
                    break
                count += 1
            face_start = len(payload) - 4 - count * 16
            faces = []
            for index in range(count):
                record = struct.unpack_from("<IIII", payload, face_start + 4 + index * 16)
                face = tuple(decode(value, args.vertices) for value in record[1:])
                if None not in face and len(set(face)) == 3:
                    faces.append(face)
            edges = {}
            for a, b, c in faces:
                for edge in ((a, b), (b, c), (c, a)):
                    edge = tuple(sorted(edge))
                    edges[edge] = edges.get(edge, 0) + 1
            bad_edges = sum(degree != 2 for degree in edges.values())
            rankings.append((bad_edges, -len(faces), position, bit, len(payload), count))
        compressed[position] = original
    for result in sorted(rankings)[:20]:
        print(result)


if __name__ == "__main__":
    main()
