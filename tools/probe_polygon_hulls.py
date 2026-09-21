"""Measure stored polygon vertices and polygon connectivity against their hull."""
from pathlib import Path
import numpy as np, struct,sys
from collections import Counter

def convex_hull(v):
 a=int(v[:,0].argmin());b=int(np.linalg.norm(v-v[a],axis=1).argmax())
 c=int(np.linalg.norm(np.cross(v[b]-v[a],v-v[a]),axis=1).argmax())
 norm=np.cross(v[b]-v[a],v[c]-v[a]);e=int(np.abs((v-v[a])@norm).argmax())
 center=v[[a,b,c,e]].mean(0)
 def orient(f):
  x,y,z=f
  return (x,z,y) if np.dot(np.cross(v[y]-v[x],v[z]-v[x]),center-v[x])>0 else f
 faces=[orient(f) for f in [(a,b,c),(a,e,b),(a,c,e),(b,e,c)]]
 for i in range(len(v)):
  if i in [a,b,c,e]:continue
  f=np.array(faces);norm=np.cross(v[f[:,1]]-v[f[:,0]],v[f[:,2]]-v[f[:,0]])
  lengths=np.linalg.norm(norm,axis=1);visible=np.sum(norm*(v[i]-v[f[:,0]]),axis=1)>lengths*1e-6
  if not visible.any():continue
  edges={}
  for x,y,z in f[visible]:
   for j,k in [(x,y),(y,z),(z,x)]:
    key=tuple(sorted((j,k)))
    if key in edges:del edges[key]
    else:edges[key]=(j,k)
  faces=[tuple(t) for t in f[~visible]]+[orient((j,k,i)) for j,k in edges.values()]
 f=np.array(faces);norm=np.cross(v[f[:,1]]-v[f[:,0]],v[f[:,2]]-v[f[:,0]])
 norm/=np.linalg.norm(norm,axis=1)[:,None]
 return f,np.column_stack((norm,-np.sum(norm*v[f[:,0]],axis=1)))

sig=bytes.fromhex('95f2b28927963b48a7a200cba02f27ab')
for p in Path(sys.argv[1]).glob('*.bin'):
 d=p.read_bytes();pos=d.find(sig)
 print('\n',p.name)
 if pos<0:continue
 length=struct.unpack_from('<I',d,pos+20)[0]
 if (length-16)%38:print('unknown length',length);continue
 n=(length-16)//38;fc=(n+4)//2;start=pos+28
 v=np.frombuffer(d,dtype='<f8',count=n*3,offset=start).reshape(-1,3).copy()
 good=np.isfinite(v)&(np.abs(v)<10000)&((v==0)|(np.abs(v)>1e-20))
 if not good.all():
  print('invalid coordinates',[(int(i),int(axis),d[start+int(i)*24+int(axis)*8:start+int(i)*24+int(axis)*8+8].hex()) for i,axis in zip(*np.where(~good))][:36])
  restored=v.copy()
  for i,axis in zip(*np.where(~good)):
   encoded=bytearray(d[start+int(i)*24+int(axis)*8:start+int(i)*24+int(axis)*8+8]);encoded[-1]=0x40
   restored[i,axis]=struct.unpack('<d',encoded)[0]
  tri,_=convex_hull(restored)
  print('high byte experiment: hull uses',len(np.unique(tri)), 'range',restored.min(0),restored.max(0))
 for axis in range(3):
  valid=np.flatnonzero(good[:,axis]);invalid=np.flatnonzero(~good[:,axis]);v[invalid,axis]=np.interp(invalid,valid,v[valid,axis])
 triangles,planes=convex_hull(v)
 print('count',n,'valid',good.all(1).sum(),'hull uses',len(np.unique(triangles)),'range',v.min(0),v.max(0))
 # The serialized polygons are variable-length index records; upper bytes may be overwritten.
 faces=[];cursor=start+24*n+4;exact=0
 for f in range(fc):
  if cursor+4>len(d):break
  sides=struct.unpack_from('<I',d,cursor)[0]&255
  if not 3<=sides<=32:print('bad sides at',f,hex(cursor),hex(struct.unpack_from('<I',d,cursor)[0]));break
  ids=np.frombuffer(d,dtype='<u4',count=sides,offset=cursor+4)&65535
  exact+=int((ids<n).all())
  faces.append(ids);cursor+=4+sides*4
 edges=Counter(tuple(sorted((int(a),int(b)))) for f in faces for a,b in zip(f,np.roll(f,-1)) if a<n and b<n)
 print('faces',len(faces),'of',fc,'index valid',exact,'edge incidence',Counter(edges.values()),'record end delta',cursor-(pos+20+length))
 # A hull plane supports a polygon only when all its points are coplanar with it.
 supported=0
 for f in faces:
  if (f>=n).any():continue
  dist=np.abs(v[f]@planes[:,:3].T+planes[:,3])
  supported+=int((dist.max(0)<.01).any())
 print('polygons supported by hull',supported)
 if (v[:,2]<-1).any():
  ids=np.flatnonzero(v[:,2]<-1)
  print('negative-Z',len(ids),[(int(i),[round(float(c),3) for c in v[i]],d[start+int(i)*24+16:start+int(i)*24+24].hex()) for i in ids[:24]])
  v[ids,2]*=-1
  tri,planes=convex_hull(v);supported=0
  for f in faces:
   if (f>=n).any():continue
   dist=np.abs(v[f]@planes[:,:3].T+planes[:,3]);supported+=int((dist.max(0)<.01).any())
  print('reflected cap: hull uses',len(np.unique(tri)),'supported polygons',supported)
