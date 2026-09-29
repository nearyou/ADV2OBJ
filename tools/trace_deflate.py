"""Read-only DEFLATE token trace for embedded ADV ZIP members."""
import struct,zlib,sys,bisect
from pathlib import Path

class Bits:
 def __init__(self,b):self.b=b;self.p=0
 def get(self,n):
  v=int.from_bytes(self.b[self.p//8:(self.p+n+7)//8],'little')>>(self.p%8);self.p+=n;return v&((1<<n)-1)
def tree(lens):
 counts=[lens.count(i) for i in range(16)];code=0;codes={};nxt={}
 for i in range(1,16):code=(code+counts[i-1])*2 if i>1 else 0;nxt[i]=code
 for sym,n in enumerate(lens):
  if n:codes[n,nxt[n]]=sym;nxt[n]+=1
 return codes
def symbol(b,t):
 v=0
 for n in range(1,16):
  v=v*2+b.get(1)
  if (n,v) in t:return t[n,v]
 raise ValueError('bad code')
LB=[3,4,5,6,7,8,9,10,11,13,15,17,19,23,27,31,35,43,51,59,67,83,99,115,131,163,195,227,258]
LE=[0]*8+[1]*4+[2]*4+[3]*4+[4]*4+[5]*4+[0]
DB=[1,2,3,4,5,7,9,13,17,25,33,49,65,97,129,193,257,385,513,769,1025,1537,2049,3073,4097,6145,8193,12289,16385,24577]
DE=[0]*4+[i for i in range(1,14) for _ in range(2)]
def decode(data):
 b=Bits(data);out=bytearray();tokens=[];blocks=[]
 while True:
  begin=b.p;final=b.get(1);kind=b.get(2);blocks.append((begin,len(out),kind))
  if kind==0:
   b.p=(b.p+7)//8*8;n=b.get(16);nn=b.get(16)
   assert n^nn==65535
   for _ in range(n):tokens.append((len(out),1,0,b.p,b.p+8));out.append(b.get(8))
  else:
   if kind==1:lt=tree([8]*144+[9]*112+[7]*24+[8]*8);dt=tree([5]*32)
   elif kind==2:
    nl=b.get(5)+257;nd=b.get(5)+1;nc=b.get(4)+4;cl=[0]*19
    for i in [16,17,18,0,8,7,9,6,10,5,11,4,12,3,13,2,14,1,15][:nc]:cl[i]=b.get(3)
    ct=tree(cl);lens=[]
    while len(lens)<nl+nd:
     s=symbol(b,ct)
     if s<16:lens.append(s)
     elif s==16:lens += [lens[-1]]*(b.get(2)+3)
     elif s==17:lens += [0]*(b.get(3)+3)
     else:lens += [0]*(b.get(7)+11)
    lt=tree(lens[:nl]);dt=tree(lens[nl:])
   else:raise ValueError('block')
   while True:
    pos=b.p;s=symbol(b,lt)
    if s==256:break
    if s<256:tokens.append((len(out),1,0,pos,b.p));out.append(s)
    else:
     n=LB[s-257]+b.get(LE[s-257]);d=symbol(b,dt);dist=DB[d]+b.get(DE[d]);tokens.append((len(out),n,dist,pos,b.p))
     for _ in range(n):out.append(out[-dist])
  if final:break
 return bytes(out),tokens,blocks
def member(path):
 b=Path(path).read_bytes();h=b.find(b'ZippedData')-30;crc,cs,us=struct.unpack_from('<III',b,h+14);n,x=struct.unpack_from('<HH',b,h+26);s=h+30+n+x
 return b[s:s+cs],us,crc
if __name__=='__main__':
 data,size,crc=member(sys.argv[1]);out,tokens,blocks=decode(data);assert out==zlib.decompress(data,-15)
 print('bytes',len(out),'expected',size,'blocks',len(blocks),flush=True)
 starts=[t[0] for t in tokens];p=out.find(bytes.fromhex('95f2b28927963b48a7a200cba02f27ab'))+16
 print('target',p,out[p:p+4].hex(),'block',[x for x in blocks if x[1]<=p][-1])
 for _ in range(30):
  i=bisect.bisect_right(starts,p)-1;t=tokens[i];print('token',t,'data',out[t[0]:t[0]+min(t[1],24)].hex())
  if t[2]==0:break
  p-=t[2]
