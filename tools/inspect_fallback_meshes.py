from pathlib import Path
import sys, struct
import numpy as np
from inspect_adv import iter_zip_members
from find_embedded_meshes import inspect

for name in sys.argv[1:]:
    path=Path(name)
    print(path.name)
    for index,(offset,member,csize,usize,payload,valid) in enumerate(iter_zip_members(path.read_bytes())):
        found=inspect(payload)
        if found:
            vertices,faces,start=found
            vertex_start=start-vertices*24
            if vertex_start<0:continue
            points=np.frombuffer(payload, dtype='<f8', count=vertices*3, offset=vertex_start).reshape((-1,3))
            if np.isfinite(points).all():
                print(index,hex(offset),member,vertices,faces,'vertex start',hex(vertex_start),
                      'bounds',np.round(points.min(0),2),np.round(points.max(0),2),'valid ZIP',valid)
