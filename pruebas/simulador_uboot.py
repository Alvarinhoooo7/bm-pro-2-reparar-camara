"""Simulador del script de auto-reparacion en U-Boot (env autofix_ver=3).

Imita el interprete simple de U-Boot 2018.07 de la camara, con lo verificado en hardware:
  - sin hush (no hay if/&&), ';' separa comandos y ${var} se expande al ejecutar cada uno
  - "run A B" ejecuta B solo si A salio bien; variable inexistente = error
  - crc32 <addr> <len> <dst> guarda la palabra nativa (little endian) en <dst>
  - itest[.l] *<addr> == <hex>  /  itest 1 == 1
  - sunxi_flash read <addr> <part> [size]   (sunxi_flash write NO existe en la practica)
  - bootm arranca si en 0x82000000 esta el boot.fex correcto, si no: "uncompress error"

Uso:  python simulador_uboot.py <imagen_flash_16MB.bin>
      (la imagen se arma con armar_imagen.py o es un volcado completo)
Prueba los casos: sana / bit real perdido / kernel y copia danados / ranura OTA extendB.
"""
import os, re, struct, sys, zlib

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FW = os.path.join(REPO, 'firmware', 'v0.1.5_extraido')
BOOT = open(os.path.join(FW, 'boot.fex'), 'rb').read()
PART = {'boot': (0x70000, 0x2B0000), 'riscv0': (0x320000, 0x90000), 'extend_backup': (0xB20000, 0x410000)}


class Boot(Exception):
    pass


def leer_env(flash):
    e = flash[0x60000:0x70000]
    ok = struct.unpack('<I', e[:4])[0] == zlib.crc32(e[4:]) & 0xffffffff
    return ok, dict(x.decode().split('=', 1) for x in e[4:].split(b'\0\0')[0].split(b'\0') if b'=' in x)


def simular(flash, etiqueta):
    flash = bytearray(flash)
    ok, env = leer_env(flash)
    if not ok:
        print(f'{etiqueta:40s} -> env con CRC invalido'); return
    mem = bytearray(0x4000000); out = []

    def mrd(a, n): o = a - 0x80000000; return bytes(mem[o:o + n])
    def mwr(a, d): o = a - 0x80000000; mem[o:o + len(d)] = d

    def lista(s):
        rc = 0
        for tok in s.split(';'):
            tok = re.sub(r'\$\{(\w+)\}', lambda m: env.get(m.group(1), ''), tok).strip()
            if tok and cmd(tok.split()) != 0:
                rc = 1
        return rc

    def cmd(a):
        c = a[0]
        if c == 'echo': out.append(' '.join(a[1:])); return 0
        if c == 'setenv': env[a[1]] = ' '.join(a[2:]); return 0
        if c == 'run':
            for v in a[1:]:
                if v not in env: out.append(f'## Error: "{v}" not defined'); return 1
                if lista(env[v]) != 0: return 1
            return 0
        if c == 'sunxi_flash' and a[1] == 'read':
            o, sz = PART[a[3]]; n = int(a[4], 16) if len(a) > 4 else sz
            mwr(int(a[2], 16), flash[o:o + n]); return 0
        if c == 'sunxi_flash':
            out.append('sunxi_flash - (muestra la ayuda: write no funciona)'); return 1
        if c == 'crc32':
            v = zlib.crc32(mrd(int(a[1], 16), int(a[2], 16))) & 0xffffffff
            if len(a) > 3: mwr(int(a[3], 16), struct.pack('<I', v))
            return 0
        if c in ('itest', 'itest.l'):
            def val(x): return struct.unpack('<I', mrd(int(x[1:], 16), 4))[0] if x.startswith('*') else int(x, 16)
            return 0 if len(a) == 4 and a[2] == '==' and val(a[1]) == val(a[3]) else 1
        if c == 'bootm':
            raise Boot('ARRANCA' if mrd(0x82000000, len(BOOT)) == BOOT else 'uncompress error -> bucle')
        out.append(f"Unknown command '{c}'"); return 1

    try:
        lista(env.get('boot_normal', '')); res = 'termino sin bootm'
    except Boot as b:
        res = str(b)
    print(f'{etiqueta:40s} -> {res}')
    for o in out: print('      ', o)


def flip(im, a, m=0x40):
    im = bytearray(im); im[a] ^= m; return bytes(im)


def ranura(im, slot):
    im = bytearray(im); e = bytes(im[0x60000:0x70000])
    body = e[4:].replace(b'extend_partition=extendA', b'extend_partition=' + slot.encode())[:0x10000 - 4]
    im[0x60000:0x70000] = struct.pack('<I', zlib.crc32(body) & 0xffffffff) + body
    return bytes(im)


if __name__ == '__main__':
    img = open(sys.argv[1], 'rb').read()
    ok, env = leer_env(img)
    print('autofix_ver =', env.get('autofix_ver'), ' bootdelay =', env.get('bootdelay'))
    simular(img, 'sana')
    simular(flip(img, 0x16BD14), 'kernel con el bit real perdido')
    simular(flip(flip(img, 0x16BD14), 0xB20000 + 0x1000), 'kernel y copia danados')
    simular(ranura(flip(img, 0x16BD14), 'extendB'), 'kernel danado, ranura OTA extendB')
