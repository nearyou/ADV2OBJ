"""Print scalar fields around active Pie/Saw names for format analysis."""
from pathlib import Path
import re
import struct
import sys


data = Path(sys.argv[1]).read_bytes()
pattern = re.compile(rb"(?:Pie|Saw)\d+-\d+")
for match in pattern.finditer(data):
    name = match.group().decode("ascii")
    offset = match.start()
    normal = struct.unpack_from("<3d", data, offset - 32)
    magnitude = sum(value * value for value in normal) ** 0.5
    if abs(magnitude - 1) > 1e-5:
        continue
    print(name, hex(offset))
    for relative in range(-96, 33, 4):
        raw = data[offset + relative:offset + relative + 8]
        if len(raw) < 8:
            continue
        integer = struct.unpack("<q", raw)[0]
        floating = struct.unpack("<d", raw)[0]
        if relative % 8 == 0:
            print(f" {relative:4}: i64={integer:20} f64={floating: .12g} hex={raw.hex()}")
    print()
