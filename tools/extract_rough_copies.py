"""Extract candidate rough copies for diagnostics; never changes ADV inputs."""
from pathlib import Path
import struct,zlib,sys
from inspect_adv import iter_zip_members

root=Path(sys.argv[2]);root.mkdir(exist_ok=True,parents=True)
prefix=bytes.fromhex('00000000000100000000000000000000')
sig=bytes.fromhex('95f2b28927963b48a7a200cba02f27ab')
for path in Path(sys.argv[1]).glob('*.adv'):
 data=path.read_bytes();items=[]
 for off,name,cs,us,b,valid in iter_zip_members(data):
  if b.startswith(prefix) and len(b)>50000 and sig in b:
   (root/(path.name+'.'+str(off)+'.bin')).write_bytes(b);items.append((off,len(b)))
 print(path.name,items,flush=True)
