"""Read-only survey of counted geometry and alignment in remaining inputs."""
from pathlib import Path
import struct
import sys
import numpy as np
from inspect_adv import iter_zip_members

def runs(good):
    change = np.diff(np.r_[False, good, False].astype(np.int8))
    return list(zip(np.flatnonzero(change == 1), np.flatnonzero(change == -1)))

for path in Path(sys.argv[1]).glob('*.adv'):
    raw = path.read_bytes()
    members = list(iter_zip_members(raw))
    if not members:
        payload = raw
    elif members[0][0] < 100000:
        payload = members[0][4]
    else:
        print(path.name, 'primary inflate fails'); continue
    print('\n', path.name, 'payload', len(payload), flush=True)
    tables=[]
    for phase in range(16):
        n=(len(payload)-phase)//16
        words=np.frombuffer(payload,dtype='<u4',count=n*4,offset=phase).reshape(-1,4)
        for a,b in runs(words[:,0]==3):
            if b-a<1000: continue
            start=phase+int(a)*16
            previous=struct.unpack_from('<I',payload,start-4)[0]
            maximum=int((words[a:b,1:]&65535).max())
            tables.append((start,int(b-a),previous,maximum))
    for start,length,count,maximum in tables[:12]:
        print('face run',hex(start),length,'count',count,'max',maximum)
        if count<length or count>100000 or count%2: continue
        n=(count+4)//2; estimate=start-4-n*24
        if estimate<8: continue
        print('vertices',n,'estimated',hex(estimate),'header',payload[estimate-8:estimate].hex())
        for delta in range(-4,5):
            if estimate+delta<0: continue
            v=np.frombuffer(payload,dtype='<f8',count=n*3,offset=estimate+delta).reshape(-1,3)
            good=np.isfinite(v)&(np.abs(v)<10000)&((v==0)|(np.abs(v)>1e-20))
            both=good.sum(1)>=2
            long=sorted(((int(b-a),int(a),int(b)) for a,b in runs(both)),reverse=True)[:3]
            print(' shift',delta,'good3',int(good.all(1).sum()),'good2',int(both.sum()),'runs',long)
