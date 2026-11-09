"""Make the Lua Computer look: a stock gate's frame (slot0) with its holo symbol (slot1) replaced by a Lua logo.
Usage: lua_gate.py <exported gate .obj> <out.obj> texture         one square showing material lua_logo's picture
       lua_gate.py <exported gate .obj> <out.obj> holo            the same square as material lua_holo: a see-through
                                                                  hologram symbol like the stock gates'
       lua_gate.py <exported gate .obj> <out.obj> [R] [centre]    the logo built from discs (no texture needed)
(Sequencer Gate: R 0.075, centre -0.02; Spaceship Computer: R 0.13, centre -0.045)"""
import math, sys

src, out = sys.argv[1], sys.argv[2]
textured = len(sys.argv) > 3 and sys.argv[3] in ('texture', 'holo')
picture = 'lua_holo' if len(sys.argv) > 3 and sys.argv[3] == 'holo' else 'lua_logo'
R = float(sys.argv[3]) if len(sys.argv) > 3 and not textured else 0.075
cx = cz = float(sys.argv[4]) if len(sys.argv) > 4 else -0.02
v, vt, vn, faces, mat = [], [], [], [], None
for line in open(src):
    p = line.split()
    if not p:
        continue
    if p[0] == 'v': v.append(line.strip())
    elif p[0] == 'vt': vt.append(line.strip())
    elif p[0] == 'vn': vn.append(line.strip())
    elif p[0] == 'usemtl': mat = p[1]
    elif p[0] == 'f' and mat == 'slot0': faces.append(('slot0', line.strip()))
    elif p[0] == 'f' and mat == 'slot1': holo = [int(q.split('/')[0]) for q in p[1:]]

# Lua logo proportions (big circle radius R): hole r 0.26R at +0.49R diagonally, moon r 0.26R at +1.08R.
seg = 48
y0 = 0.1407
discs = [('lua_blue', cx, cz, R, y0),
         ('lua_white', cx + 0.49 * R, cz + 0.49 * R, 0.26 * R, y0 + 0.0004),
         ('lua_blue', cx + 1.08 * R, cz + 1.08 * R, 0.26 * R, y0)]

vn.append('vn 0 1 0')
up = len(vn)
vt.append('vt 0.5 0.5')
uv = len(vt)

# A white plate where the holo square was, so the logo sits on white as the real one does.
h = max(abs(float(c)) for i in holo for c in (v[i - 1].split()[1], v[i - 1].split()[3]))
first = len(v) + 1
for x, z in ((-h, -h), (-h, h), (h, h), (h, -h)):
    v.append(f'v {x:.6f} {y0 - 0.0003:.6f} {z:.6f}')
if textured:
    # The picture's u runs along -x and v along +z: Looks mirrors the OBJ's x into Unity, so u along +x showed the
    # picture back to front in game.
    corner = len(vt) + 1
    vt += ['vt 1 0', 'vt 1 1', 'vt 0 1', 'vt 0 0']
    faces.append((picture, ' '.join(['f'] + [f'{first + k}/{corner + k}/{up}' for k in range(4)])))
    discs = []
else:
    faces.append(('lua_back', f'f {first}/{uv}/{up} {first + 1}/{uv}/{up} {first + 2}/{uv}/{up} {first + 3}/{uv}/{up}'))
for m, x, z, r, y in discs:
    centre = len(v) + 1
    v.append(f'v {x:.6f} {y:.6f} {z:.6f}')
    for i in range(seg):
        a = 2 * math.pi * i / seg
        v.append(f'v {x + r * math.cos(a):.6f} {y:.6f} {z + r * math.sin(a):.6f}')
    for i in range(seg):
        a, b = centre + 1 + i, centre + 1 + (i + 1) % seg
        # Counter-clockwise seen from above (+y): centre, next, this.
        faces.append((m, f'f {centre}/{uv}/{up} {b}/{uv}/{up} {a}/{uv}/{up}'))

with open(out, 'w', newline='\n') as f:
    f.write('# Lua Computer look: a stock gate frame (slot0) with a Lua logo of glowing discs in place of its holo\n'
            f'# symbol. Made by lua_gate.py from {src.replace(chr(92), "/").split("/")[-1]}.\n'
            'mtllib EvoCore.LuaComputer.mtl\n')
    f.write('\n'.join(v + vt + vn) + '\n')
    last = None
    for m, face in faces:
        if m != last:
            f.write(f'usemtl {m}\n')
            last = m
        f.write(face + '\n')
print(f'{len(v)} vertices, {len(faces)} faces -> {out}')
