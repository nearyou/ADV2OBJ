"""Inspect polygon mesh records, including meshes before the triangle cache."""
import struct, sys
from pathlib import Path
import numpy as np

SIGNATURE=bytes.fromhex('95f2b28927963b48a7a200cba02f27ab')
for path in Path(sys.argv[1]).glob('*.bin'):
 data=path.read_bytes(); position=0
 print('\n',path.name,flush=True)
 while (position:=data.find(SIGNATURE,position))>=0:
  header=position+20
  if header+8>=len(data):break
  length,count=struct.unpack_from('<II',data,header)
  n=count&65535;start=header+8
  print('record',hex(position),'bytes',length,'count',hex(count),'low',n, 'start',hex(start))
  if 3<n<30000 and start+n*24+4<len(data):
   v=np.frombuffer(data,dtype='<f8',offset=start,count=n*3).reshape(-1,3)
   good=np.isfinite(v)&(np.abs(v)<10000)&((v==0)|(np.abs(v)>1e-20))
   print(' valid vertices',int(good.all(1).sum()),'tail',data[start+n*24:start+n*24+40].hex(' '))
   print(' valid ranges',[(round(float(v[good[:,a],a].min()),2),round(float(v[good[:,a],a].max()),2)) if good[:,a].any() else None for a in range(3)])
   for shift in range(-128,1):
    off=start+n*24+shift; fc=struct.unpack_from('<I',data,off)[0]&65535
    if fc<4 or fc>n*2:continue
    cursor=off+4;fs=[]
    while cursor+4<len(data) and len(fs)<fc:
     size=struct.unpack_from('<I',data,cursor)[0]
     if not 3<=size<=32 or cursor+4+size*4>len(data):break
     indices=struct.unpack_from('<'+'I'*size,data,cursor+4)
     if max(indices)>=n or len(set(indices))<3:break
     fs.append(indices);cursor+=4+size*4
    if len(fs)>50:print(' polygon prefix',shift,'count',fc,'decoded',len(fs),'sides',sorted(set(map(len,fs))),'end',hex(cursor))
  position+=1
