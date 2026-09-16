from pathlib import Path
import re, struct, sys
import numpy as np
from measure_output import mesh

stem=sys.argv[1]
raw=(Path('Input')/(stem+'.adv')).read_bytes()
cuts=[]
for p in sorted((Path('Output')/stem).glob('*.obj')):
    name=p.stem[len(stem)+1:]
    if name=='Rough': continue
    offset=raw.find(name.encode())
    normal=np.array(struct.unpack_from('<3d',raw,offset-32))
    for start in range(offset-80,offset-47):
        w,d=struct.unpack_from('<2d',raw,start)
        if 1<=w<=500 and abs(d)<1e7:
            break
    cuts.append((name,w,d,normal,p))
for name,w,d,n,p in cuts:
    v,f=mesh(p)
    print(name, 'n',n,'width',w,'distance',d)
    for other,ow,od,on,_ in cuts:
        dist=v@on-od
        print(' ',other, np.round([dist.min(),dist.max()],3), 'on',np.sum(abs(dist)<.01),np.sum(abs(dist+ow)<.01))
