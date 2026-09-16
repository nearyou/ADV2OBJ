from pathlib import Path
import sys,struct
import numpy as np
from inspect_adv import iter_zip_members

path=Path(sys.argv[1]); payload=next(iter_zip_members(path.read_bytes()))[4]
print(path.name,'payload',len(payload))
for phase in range(16):
    count=(len(payload)-phase)//16
    table=np.frombuffer(payload,dtype='<u4',count=count*4,offset=phase).reshape(-1,4)
    markers=table[:,0]
    good=markers==3
    changes=np.diff(np.r_[False,good,False].astype(np.int8))
    runs=[(b-a,phase+int(a)*16) for a,b in zip(np.flatnonzero(changes==1),np.flatnonzero(changes==-1))]
    length,pos=max(runs,default=(0,0))
    tail=table[-1000:]
    print('phase',phase,'longest run',length,hex(pos),'tail marker3',int(np.sum(tail[:,0]==3)),
          'tail marker counts',[(hex(int(k)),int(v)) for k,v in zip(*np.unique(tail[:,0],return_counts=True)) if v>10][:15])
print('last words',struct.unpack_from('<64I',payload,len(payload)-256))
