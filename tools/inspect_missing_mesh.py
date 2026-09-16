"""Inspect mesh-free ADV containers without modifying them."""
from pathlib import Path
from collections import Counter
import math, struct, sys

for text in sys.argv[1:]:
    path=Path(text); raw=path.read_bytes()
    meta=struct.unpack_from('<I',raw,0x34)[0]
    print('\n',path.name,len(raw),'metadata',hex(meta))
    for offset in (0,0x30,meta,meta+32,meta+64,meta+96,meta+128,meta+256,meta+512,meta+1024,0x20000,0x40000,0x100000,len(raw)-128):
        if offset<len(raw):print(hex(offset),raw[offset:offset+64].hex(' '))
    for begin,end in [(meta,meta+4096),(0x100000,0x200000),(len(raw)//2,len(raw)//2+65536)]:
        block=raw[begin:min(end,len(raw))]
        hist=Counter(block)
        entropy=-sum((v/len(block))*math.log2(v/len(block)) for v in hist.values())
        print('entropy',hex(begin),round(entropy,3),'zeros',hist[0])
    for token in (b'ZippedData',b'Pie',b'Saw',b'PK\x03\x04',b'PK\x01\x02',b'Rough'):
        print(token,raw.count(token))
