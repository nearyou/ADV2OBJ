"""Locate long raw triangle tables in an uncompressed ADV."""
from pathlib import Path
import sys,struct
import numpy as np

for text in sys.argv[1:]:
    path=Path(text); data=path.read_bytes(); meta=struct.unpack_from('<I',data,0x34)[0]
    # The first rough record is near metadata; later records can be far away.
    block=data[meta:min(len(data),meta+50_000_000)]
    matches=[]
    for phase in range(16):
        count=(len(block)-phase)//16
        words=np.frombuffer(block,dtype='<u4',count=count*4,offset=phase).reshape(-1,4)
        good=words[:,0]==3
        changes=np.diff(np.r_[False,good,False].astype(np.int8))
        for a,b in zip(np.flatnonzero(changes==1),np.flatnonzero(changes==-1)):
            if b-a<100:continue
            pos=meta+phase+int(a)*16
            preceding=struct.unpack_from('<I',data,pos-4)[0] if pos>=4 else -1
            segment=words[a:b]
            largest=int(segment[:,1:].max())
            matches.append((b-a,pos,preceding,largest))
    print('\n',path.name,'runs',len(matches))
    for length,pos,preceding,largest in sorted(matches,reverse=True)[:15]:
        print(length,hex(pos),'preceding',preceding,'largest index',largest,
              'closed-size-expected',(length+4)//2)
