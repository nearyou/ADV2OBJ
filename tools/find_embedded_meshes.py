"""List embedded ZippedData records that contain a closed indexed mesh."""
import struct, sys
from pathlib import Path
from inspect_adv import iter_zip_members

MASKS=(0,0x00400000,0x00408800,0x40880000,0x40000000,0x40408800,
       0x40400000,0x82000000,0x82400000,0x82407600,0x00824000,0x00820000)
MARKERS={m^3 for m in MASKS}

def unique(value, limit):
    values={value^m for m in MASKS if value^m<limit}
    return next(iter(values)) if len(values)==1 else None

def inspect(payload):
    count=0
    for pos in range(len(payload)-16,-1,-16):
        if struct.unpack_from('<I',payload,pos)[0] not in MARKERS: break
        count+=1
    if count<4 or count%2: return None
    start=len(payload)-4-count*16
    if start<0 or unique(struct.unpack_from('<I',payload,start)[0],count+1)!=count:return None
    vertices=(count+4)//2
    faces=[]
    for i in range(count):
        rec=struct.unpack_from('<4I',payload,start+4+16*i)
        face=tuple(unique(v,vertices) for v in rec[1:])
        if None in face or len(set(face))<3:return None
        faces.append(face)
    edges={}
    for face in faces:
        for a,b in zip(face,(face[1],face[2],face[0])):
            key=tuple(sorted((a,b)));edges[key]=edges.get(key,0)+1
    if any(n!=2 for n in edges.values()):return None
    return vertices,count,start

if __name__ == '__main__':
    for arg in sys.argv[1:]:
        path=Path(arg); print(path)
        for i,(base,name,csize,size,payload,valid) in enumerate(iter_zip_members(path.read_bytes())):
            result=inspect(payload)
            if result: print(i,hex(base),len(payload),valid,result)
