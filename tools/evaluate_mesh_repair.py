"""Evaluate topology-neighbour coordinate repair against paired sample OBJ files."""

from __future__ import annotations

import math
import statistics
import struct
import sys
import zlib
from pathlib import Path


def obj_data(path):
    vertices, faces = [], []
    for line in path.read_text().splitlines():
        fields = line.split()
        if fields[:1] == ["v"]:
            vertices.append(tuple(map(float, fields[1:4])))
        elif fields[:1] == ["f"]:
            faces.append(tuple(int(x) - 1 for x in fields[1:4]))
    return vertices, faces


def payload(path, partial=False):
    data = path.read_bytes()
    if partial:
        method_offset = 0xF19C
        fields = struct.unpack_from("<HHHIIIHH", data, method_offset)
        start = method_offset + 22 + fields[6] + fields[7]
        size = fields[4]
    else:
        offset = data.find(b"PK\x03\x04")
        fields = struct.unpack_from("<IHHHHHIIIHH", data, offset)
        start = offset + 30 + fields[9] + fields[10]
        size = fields[7]
    return zlib.decompress(data[start : start + size], -15)


def plausible(value):
    return math.isfinite(value) and abs(value) < 1e7 and (value == 0 or abs(value) > 1e-20)


def main():
    root = Path(sys.argv[1] if len(sys.argv) > 1 else ".")
    cases = {
        "A196-186": (0, 0, True),
        "A196-189": (56, 3190, False),
        "M110-32219": (0, 0, False),
        "M110-32225": (0, 0, False),
        "SH3371-393-LS": (54, 8590, False),
    }
    for name, (gap, split, partial) in cases.items():
        expected, faces = obj_data(root / "Output" / name / f"{name}_Rough.obj")
        data = payload(root / "Input" / f"{name}.adv", partial)
        face_start = len(data) - 4 - len(faces) * 16
        count = len(expected)
        start = face_start - count * 24 + gap
        vertices = [None] * count
        if gap:
            for i in range(split):
                vertices[i] = list(struct.unpack_from("<ddd", data, start + i * 24))
            for i in range(split + 4, count):
                at = face_start - (count - i) * 24
                vertices[i] = list(struct.unpack_from("<ddd", data, at))
            left, right = vertices[split - 1], vertices[split + 4]
            for j in range(4):
                ratio = (j + 1) / 5
                vertices[split + j] = [left[k] + (right[k] - left[k]) * ratio for k in range(3)]
        else:
            vertices = [list(struct.unpack_from("<ddd", data, start + i * 24)) for i in range(count)]

        neighbours = [set() for _ in vertices]
        for a, b, c in faces:
            neighbours[a].update((b, c))
            neighbours[b].update((a, c))
            neighbours[c].update((a, b))

        repaired = set()
        for _ in range(3):
            changes = []
            for i, vertex in enumerate(vertices):
                if not gap or not (split <= i < min(len(vertices), split + 1000)):
                    continue
                for axis in range(3):
                    values = [vertices[n][axis] for n in neighbours[i] if plausible(vertices[n][axis])]
                    if len(values) < 3:
                        continue
                    median = statistics.median(values)
                    deviations = [abs(value - median) for value in values]
                    mad = statistics.median(deviations)
                    value = vertex[axis]
                    if not plausible(value) or abs(value - median) > max(30.0, 3.0 * max(mad, 1.0)):
                        changes.append((i, axis, median))
            if not changes:
                break
            for i, axis, value in changes:
                vertices[i][axis] = value
                repaired.add(i)

        errors = [math.dist(vertices[i], expected[i]) for i in range(count)]
        print(name, "repaired", len(repaired), "max", max(errors), "rms", math.sqrt(sum(e*e for e in errors)/len(errors)))


if __name__ == "__main__":
    main()
