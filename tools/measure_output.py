"""Read-only numerical comparison of same-named OBJ exports."""
from pathlib import Path
import sys
import numpy as np

def mesh(path):
    v, f = [], []
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        fields = line.split()
        if fields[:1] == ['v']:
            v.append(list(map(float, fields[1:4])))
        elif fields[:1] == ['f']:
            f.append([int(x.split('/')[0])-1 for x in fields[1:4]])
    return np.asarray(v), np.asarray(f)

def volume(v, f):
    return np.einsum('ij,ij->i', v[f[:, 0]], np.cross(v[f[:, 1]], v[f[:, 2]])).sum()/6

if __name__ == '__main__':
    for path in sorted(Path(sys.argv[1]).rglob('*.obj')):
        other = Path(sys.argv[2])/path.relative_to(sys.argv[1])
        if not other.exists():
            print(path.name, 'MISSING')
            continue
        a, af = mesh(path)
        b, bf = mesh(other)
        print(path.name, 'V/F', len(a),len(af),len(b),len(bf), 'bbox delta',
              round(float(np.max(np.abs(np.r_[a.min(0)-b.min(0),a.max(0)-b.max(0)]))),5),
              'volume ratio',round(volume(b,bf)/volume(a,af),6))
        if a.shape == b.shape:
            errors = np.linalg.norm(a-b, axis=1)
            print('  same-index error quantiles', np.quantile(errors,[0,.5,.9,.99,1]), 'worst',errors.argmax())
