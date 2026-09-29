"""Read-only survey of stored polygon record boundaries in extracted payloads."""
from pathlib import Path
import struct,sys
import numpy as np

SIG=bytes.fromhex('95f2b28927963b48a7a200cba02f27ab')
for path in Path(sys.argv[1]).glob('*.bin'):
 b=path.read_bytes(); h=b.find(SIG)
 if h<0:print(path.stem,'NO HEADER');continue
 length=struct.unpack_from('<I',b,h+20)[0]; n=(length-16)//38
 if not 8<=n<=10000 or (length-16)%38: print(path.stem,'BAD LENGTH',length);continue
 expected=h+24+length; actual=b.find(SIG,h+16)
 v=np.frombuffer(b,dtype='<f8',offset=h+28,count=n*3).reshape(-1,3)
 good=np.isfinite(v)&(np.abs(v)<10000)&((v==0)|(np.abs(v)>1e-20))
 t=h+28+n*24; fc=n//2+2; parsed=0; ids=[];p=t+4
 while p+4<len(b) and parsed<fc:
  size=b[p]
  if not 3<=size<=64 or p+4+size*4>len(b):break
  ids.extend(struct.unpack_from('<'+'I'*size,b,p+4));p+=4+size*4;parsed+=1
 print(path.stem,'N',n,'bad',int((~good.all(1)).sum()),'faces',parsed,fc,'end',p-expected,'next',actual-expected,'badIds',sum(x>=n for x in ids))
