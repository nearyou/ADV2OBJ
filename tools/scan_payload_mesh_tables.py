import struct,sys
from pathlib import Path
from inspect_adv import iter_zip_members
from find_embedded_meshes import MARKERS, unique

data=Path(sys.argv[1]).read_bytes(); payload=next(iter_zip_members(data))[4]
runs=[]
for phase in range(16):
    start=None
    for p in range(phase,len(payload)-15,16):
        marker=struct.unpack_from('<I',payload,p)[0]
        if marker in MARKERS:
            if start is None:start=p
        elif start is not None:
            count=(p-start)//16
            if count>=4:runs.append((start,p,count))
            start=None
    if start is not None:
        end = len(payload)
        count = (end-start)//16
        if count>=4:runs.append((start,end,count))
for start,end,count in sorted(runs,key=lambda x:-x[2])[:100]:
    for before in range(max(0,start-32),start):
        encoded=struct.unpack_from('<I',payload,before)[0]
        if unique(encoded,count+1)==count:
            vertices=(count+4)//2
            valid=0
            for i in range(count):
                rec=struct.unpack_from('<4I',payload,start+16*i)
                face=[unique(v,vertices) for v in rec[1:]]
                valid+=None not in face and len(set(face))==3
            print(hex(before),hex(start),count,vertices,'valid',valid)
