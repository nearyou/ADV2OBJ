"""Compare a recoverable one-bit DEFLATE candidate with a reference mesh."""

from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path

from search_mesh_candidates import approximate_hits


MASKS = (0, 0x00400000, 0x00408800, 0x40880000, 0x40000000,
         0x40408800, 0x40400000, 0x82000000, 0x82400000,
         0x82407600, 0x00824000, 0x00820000)
MARKERS = {mask ^ 3 for mask in MASKS}


def reference_faces(path: Path):
    result = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        fields = line.split()
        if fields[:1] == ["f"]:
            result.append(tuple(int(value) - 1 for value in fields[1:4]))
    return result


def reference_vertices(path: Path):
    result = []
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        fields = line.split()
        if fields[:1] == ["v"]:
            result.append(tuple(map(float, fields[1:4])))
    return result


def decode(value: int, limit: int):
    candidates = {value ^ mask for mask in MASKS if (value ^ mask) < limit}
    return next(iter(candidates)) if len(candidates) == 1 else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("adv", type=Path)
    parser.add_argument("obj", type=Path)
    parser.add_argument("offset", type=lambda value: int(value, 0))
    parser.add_argument("bit", type=int)
    args = parser.parse_args()
    raw = args.adv.read_bytes()
    header = raw.find(b"PK\x03\x04")
    compressed_size = struct.unpack_from("<I", raw, header + 18)[0]
    name_len, extra_len = struct.unpack_from("<HH", raw, header + 26)
    start = header + 30 + name_len + extra_len
    compressed = bytearray(raw[start:start + compressed_size])
    compressed[args.offset] ^= 1 << args.bit
    payload = zlib.decompress(compressed, -15)
    expected = reference_faces(args.obj)
    vertex_count = (len(expected) + 4) // 2
    face_start = len(payload) - 4 - len(expected) * 16
    valid = exact = 0
    bad = []
    for index, expected_face in enumerate(expected):
        offset = face_start + 4 + index * 16
        marker, *encoded = struct.unpack_from("<IIII", payload, offset)
        mask = marker ^ 3
        decoded = tuple(value ^ mask for value in encoded)
        okay = marker in MARKERS and None not in decoded
        valid += okay
        exact += okay and decoded == expected_face
        if not okay or decoded != expected_face:
            bad.append((index, marker, decoded, expected_face))
    print(f"payload={len(payload)} expected faces={len(expected)} face_start={face_start}")
    print(f"valid records={valid} exact sequence={exact} bad={len(bad)}")
    print("first bad", bad[:20])
    print("last bad", bad[-20:])
    for shift in range(-32, 33):
        shifted_exact = 0
        compared = 0
        for index in range(len(expected)):
            expected_index = index + shift
            if not 0 <= expected_index < len(expected):
                continue
            offset = face_start + 4 + index * 16
            marker, *encoded = struct.unpack_from("<IIII", payload, offset)
            mask = marker ^ 3
            decoded = tuple(value ^ mask for value in encoded)
            compared += 1
            shifted_exact += marker in MARKERS and decoded == expected[expected_index]
        if shifted_exact > len(expected) * 0.9:
            print(f"shift={shift}: exact={shifted_exact}/{compared}")

    actual_start = len(payload) - 4 - 20085 * 16
    candidate_faces = []
    for index in range(20085):
        offset = actual_start + 4 + index * 16
        marker, *encoded = struct.unpack_from("<IIII", payload, offset)
        mask = marker ^ 3
        decoded = tuple(value ^ mask for value in encoded)
        if marker in MARKERS and max(decoded) < vertex_count and len(set(decoded)) == 3:
            candidate_faces.append(decoded)
    edges = {}
    for face in candidate_faces:
        for a, b in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])):
            edge = tuple(sorted((a, b)))
            edges[edge] = edges.get(edge, 0) + 1
    histogram = {}
    for degree in edges.values():
        histogram[degree] = histogram.get(degree, 0) + 1
    reference_set = set(expected)
    print(f"actual candidate faces={len(candidate_faces)} ref members={sum(face in reference_set for face in candidate_faces)}")
    print(f"edge degree histogram={histogram}")
    verts_for_lengths = reference_vertices(args.obj)
    import math
    correct_lengths = []
    wrong_lengths = []
    for face in candidate_faces:
        lengths = [math.dist(verts_for_lengths[face[i]], verts_for_lengths[face[(i + 1) % 3]]) for i in range(3)]
        (correct_lengths if face in reference_set else wrong_lengths).append(max(lengths))
    for label, values in (("correct", correct_lengths), ("wrong", wrong_lengths)):
        values.sort()
        print(label, "max-edge percentiles", [values[int((len(values)-1)*p)] for p in (0.5, 0.9, 0.95, 0.99, 1)])
    for threshold in (200, 250, 300, 350, 400, 500, 700, 1000):
        print(
            "threshold", threshold,
            "correct kept", sum(value <= threshold for value in correct_lengths),
            "wrong kept", sum(value <= threshold for value in wrong_lengths),
        )
    vertices = reference_vertices(args.obj)
    vertex_hits = []
    for index in list(range(min(20, len(vertices)))) + list(range(max(20, len(vertices) - 20), len(vertices))):
        hits = list(approximate_hits(payload, vertices[index]))
        if hits:
            vertex_hits.append((index, hits[:2]))
    print("reference vertex hits", vertex_hits)


if __name__ == "__main__":
    main()
