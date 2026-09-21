"""Read-only dump of serialized cut collections in ADV files."""

from pathlib import Path
import struct
import sys


GROUP = bytes.fromhex("A0C08CABAB842A4692BCD35D2AFAADCB")
RECORD = bytes.fromhex("2865304F9501CB43B95997C5EA20E0F3")


def u32(data, offset):
    return struct.unpack_from("<I", data, offset)[0]


def f64(data, offset):
    return struct.unpack_from("<d", data, offset)[0]


for argument in sys.argv[1:]:
    path = Path(argument)
    data = path.read_bytes()
    print("\n", path.name)
    cursor = 0
    while True:
        start = data.find(GROUP, cursor)
        if start < 0:
            break
        cursor = start + 16
        if start + 28 > len(data):
            continue
        length = u32(data, start + 20)
        end = start + 24 + length
        if length < 12 or length > 100_000 or end > len(data):
            continue
        count = u32(data, start + 24)
        piece = struct.unpack_from("<i", data, end - 4)[0]
        records = []
        position = start + 28
        for _ in range(count if count < 1000 else 0):
            if position + 108 > end or data[position:position + 16] != RECORD:
                break
            record_length = u32(data, position + 20)
            name_length = u32(data, position + 80)
            if name_length > 100 or position + 28 + record_length > end:
                break
            name = data[position + 84:position + 84 + name_length].decode("ascii", "replace")
            branch = u32(data, position + 24 + record_length)
            records.append((name, branch, f64(data, position + 28),
                            f64(data, position + 36),
                            tuple(round(f64(data, position + axis), 6)
                                  for axis in (52, 60, 68))))
            position += 28 + record_length
        if len(records) == count:
            print(hex(start), "piece", piece, "cuts", records)
