"""Check generated OBJ indices, coordinates, and closed edge topology."""
from collections import Counter
from pathlib import Path
import sys

import numpy as np

from measure_output import mesh

root = Path(sys.argv[1])
objects = sorted(root.rglob('*.obj'))
bad = []
for path in objects:
    vertices, faces = mesh(path)
    if len(vertices) < 4 or len(faces) < 4 or not np.isfinite(vertices).all():
        bad.append((path.name, 'empty or nonfinite'))
        continue
    if faces.min() < 0 or faces.max() >= len(vertices):
        bad.append((path.name, 'face index out of range'))
        continue
    edges = Counter(tuple(sorted((int(a), int(b)))) for face in faces
                    for a, b in ((face[0], face[1]), (face[1], face[2]), (face[2], face[0])))
    wrong = sum(count != 2 for count in edges.values())
    if wrong:
        bad.append((path.name, f'{wrong} nonmanifold edges'))
print('OBJ files', len(objects), 'invalid', len(bad))
for name, reason in bad:
    print('FAIL', name, reason)
if bad:
    sys.exit(1)
