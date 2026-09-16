from pathlib import Path
import sys,struct
import numpy as np
from inspect_adv import iter_zip_members
from find_embedded_meshes import MARKERS

for text in sys.argv[1:]:
    path=Path(text); rankings=[]
    for i,(offset,name,csize,usize,payload,valid) in enumerate(iter_zip_members(path.read_bytes())):
        best=(0,0,0)
        for phase in range(16):
            count=(len(payload)-phase)//16
            if count<100:continue
            words=np.frombuffer(payload,dtype='<u4',count=count*4,offset=phase).reshape(-1,4)
            good=np.isin(words[:,0],tuple(MARKERS))
            changes=np.diff(np.r_[False,good,False].astype(np.int8))
            for a,b in zip(np.flatnonzero(changes==1),np.flatnonzero(changes==-1)):
                if b-a>best[0]:best=(int(b-a),phase+int(a)*16,phase+int(b)*16)
        if best[0]>100:rankings.append((best[0],i,offset,name,len(payload),valid,best[1],best[2]))
    print(path.name,'members',i+1,'candidates',len(rankings))
    for row in sorted(rankings,reverse=True)[:20]:print(row)
