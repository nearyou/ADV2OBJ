from pathlib import Path
import sys, math
from inspect_adv import iter_zip_members
from compare_reference_meshes import vertices
from search_mesh_candidates import approximate_hits

adv=Path(sys.argv[1]); root=Path(sys.argv[2]); target=Path(sys.argv[3])
rough=vertices(next(root.glob('*_Rough.obj'))); planned=vertices(target)
shared=[]
for p in planned:
    if min(math.dist(p,q) for q in rough)<.001: shared.append(p)
print('shared',len(shared))
members=list(iter_zip_members(adv.read_bytes()))
for i,m in enumerate(members):
    count=sum(bool(list(approximate_hits(m[4],p))) for p in shared[:100])
    if count:print(i,hex(m[0]),len(m[4]),m[5],count)
