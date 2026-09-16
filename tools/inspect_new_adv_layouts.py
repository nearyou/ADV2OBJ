"""Summarize Advisor 8.1 container and first rough-mesh payload layout."""
from pathlib import Path
import struct
import sys
import zlib

for path in sorted(Path(sys.argv[1]).glob('*.adv')):
    data = path.read_bytes()
    metadata = struct.unpack_from('<I', data, 0x34)[0]
    print('\n', path.name, len(data), 'metadata', hex(metadata), 'at', data[metadata:metadata+32].hex(' '))
    for name in (b'ZippedData', b'PK\x03\x04'):
        positions=[]; start=0
        while len(positions)<5:
            offset=data.find(name,start)
            if offset<0:break
            positions.append(hex(offset));start=offset+1
        print(' ',name,positions)
    offset=data.find(b'ZippedData',max(0,metadata-1024),min(len(data),metadata+65536))
    if offset<0:continue
    header=offset-30
    print('  around zipped',data[header:offset+20].hex(' '))
    if data[header:header+4]==b'PK\x03\x04':
        method=struct.unpack_from('<H',data,header+8)[0]
        csize=struct.unpack_from('<I',data,header+18)[0]
        usize=struct.unpack_from('<I',data,header+22)[0]
        nlen,elen=struct.unpack_from('<HH',data,header+26)
        begin=header+30+nlen+elen
    else:
        method=struct.unpack_from('<H',data,offset-22)[0]
        csize=struct.unpack_from('<I',data,offset-12)[0]
        usize=struct.unpack_from('<I',data,offset-8)[0]
        nlen,elen=struct.unpack_from('<HH',data,offset-4)
        begin=offset+nlen+elen
    print('  member',method,csize,usize,nlen,elen,'begin',hex(begin))
    if csize<len(data):
        try:
            payload=zlib.decompress(data[begin:begin+csize],-15)
            print('  payload',len(payload),'head',payload[:64].hex(' '),'tail',payload[-64:].hex(' '))
            print('  trailing ints',struct.unpack_from('<8I',payload,len(payload)-32))
        except zlib.error as error:print('  inflate error',error)
