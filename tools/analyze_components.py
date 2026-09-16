"""Report face-connected components in generated OBJ meshes."""
from collections import defaultdict, deque
from pathlib import Path
import sys

import numpy as np

from measure_output import mesh, volume


def components(faces):
    by_vertex = defaultdict(list)
    for face_index, face in enumerate(faces):
        for vertex in face:
            by_vertex[int(vertex)].append(face_index)
    unseen = set(range(len(faces)))
    while unseen:
        first = unseen.pop()
        found = {first}
        queue = deque([first])
        while queue:
            face_index = queue.popleft()
            for vertex in faces[face_index]:
                for adjacent in by_vertex[int(vertex)]:
                    if adjacent in unseen:
                        unseen.remove(adjacent)
                        found.add(adjacent)
                        queue.append(adjacent)
        yield sorted(found)


for path_text in sys.argv[1:]:
    path = Path(path_text)
    vertices, faces = mesh(path)
    print(path)
    for indexes in components(faces):
        selected_faces = faces[indexes]
        used = np.unique(selected_faces)
        remap = {int(old): new for new, old in enumerate(used)}
        local_faces = np.array([[remap[int(value)] for value in face] for face in selected_faces])
        local_vertices = vertices[used]
        print(
            " ", len(local_vertices), len(local_faces),
            "bbox", np.round(np.r_[local_vertices.min(0), local_vertices.max(0)], 3),
            "volume", round(abs(float(volume(local_vertices, local_faces))), 3),
        )
