from pathlib import Path
import struct,sys,numpy as np
from collections import Counter,defaultdict
for p in Path(sys.argv[1]).glob('*.bin'):
 d=p.read_bytes();found=[]
 for phase in range(16):
  w=np.frombuffer(d,dtype='<u4',count=(len(d)-phase)//16*4,offset=phase).reshape(-1,4)
  good=w[:,0]==3; changes=np.diff(np.r_[False,good,False].astype(np.int8))
  for a,b in zip(np.flatnonzero(changes==1),np.flatnonzero(changes==-1)):
   off=phase+int(a)*16;fc=struct.unpack_from('<I',d,off-4)[0] if off>=4 else 0
   if b-a>=1000 and b-a<=fc<50000 and fc%2==0:found.append((off,fc))
 if not found:continue
 off,fc=min(found);n=(fc+4)//2;end=min(len(d)-16,off+fc*16+512);pos=off;faces=[];keys=set()
 while pos<=end:
  m,a,b,c=struct.unpack_from('<4I',d,pos)
  if m==3 and max(a,b,c)<n and len({a,b,c})==3:
   key=tuple(sorted((a,b,c)))
   if key not in keys:faces.append((a,b,c));keys.add(key)
   pos+=16
  else:pos+=1
 edges=Counter(tuple(sorted((a,b))) for f in faces for a,b in zip(f,f[1:]+f[:1]))
 boundary=[e for e,c in edges.items() if c==1];degrees=Counter(v for e in boundary for v in e)
 print(p.name,'faces',len(faces),'/',fc,'used',len(set(v for f in faces for v in f)),'edge incidence',Counter(edges.values()),'boundary vertex degree',Counter(degrees.values()))
