"""Pruebas de afx en el PC, sobre una imagen de flash armada con los volcados reales.

Uso:  python test_afx.py [ruta afx_pc.exe]
Necesita volcados/placa_prueba/ (placa sana) y volcados/camara_89a17d3/extend.bin (/usr real con 2 bits perdidos).
Cada caso parte de la imagen "protegida" (con init hecho), dana algo, corre 'repair' y comprueba el resultado.
"""
import os, shutil, subprocess, sys, tempfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AFX = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'herramienta', 'afx', 'afx_pc.exe')
V = os.path.join(REPO, 'volcados', 'placa_prueba')
BLK = 0x10000
PART = {'uboot': 0x0, 'private': 0x50000, 'env': 0x60000, 'boot': 0x70000, 'riscv0': 0x320000,
        'rootfs': 0x3B0000, 'extend': 0x710000, 'bak': 0xB20000, 'rootfs_data': 0xF30000, 'UDISK': 0xF90000}

tmp = tempfile.mkdtemp(prefix='afx_test_')
IMG, MAN = os.path.join(tmp, 'img.bin'), os.path.join(tmp, 'manifest')


def leer(n):
    return open(os.path.join(V, n), 'rb').read()


def armar_base():
    img = bytearray(b'\xff' * 0x1000000)
    for n, f in [('uboot', 'uboot.bin'), ('private', 'private.bin'), ('env', 'env_v3_produccion.bin'),
                 ('boot', 'boot.bin'), ('riscv0', 'riscv0.bin'), ('rootfs', 'rootfs.bin'), ('extend', 'extend.bin'),
                 ('rootfs_data', 'rootfs_data.bin'), ('UDISK', 'UDISK.bin')]:
        d = leer(f); img[PART[n]:PART[n] + len(d)] = d
    bak = leer('boot.bin') + leer('riscv0.bin')               # copia como la instala el .exe
    img[PART['bak']:PART['bak'] + len(bak)] = bak
    return img


def afx(*args):
    r = subprocess.run([AFX, '-img', IMG, '-m', MAN] + list(args), capture_output=True, text=True)
    return r.returncode, r.stdout + r.stderr


def guardar(img): open(IMG, 'wb').write(img)
def cargar(): return bytearray(open(IMG, 'rb').read())


ok_total = 0; fallos = []
def caso(nombre, cond, salida=''):
    global ok_total
    print(('  OK   ' if cond else '  FALLA') + ' ' + nombre)
    if cond: ok_total += 1
    else:
        fallos.append(nombre); print('        ' + salida.replace('\n', '\n        '))


print('afx:', AFX, '\nimagen temporal:', IMG)
base = armar_base(); guardar(base)
rc, out = afx('init'); caso('init', rc == 0, out)
protegida = cargar()
caso('init solo escribio extend_backup bloques 52-64',
     protegida[:PART['bak'] + 52 * BLK] == base[:PART['bak'] + 52 * BLK] and protegida[PART['bak'] + 65 * BLK:] == base[PART['bak'] + 65 * BLK:])
rc, out = afx('check'); caso('check sobre imagen sana = OK', rc == 0 and 'todo OK' in out, out)


def probar(nombre, danar, rc_esperado, comparar=True, extra=None):
    img = bytearray(protegida); danar(img); guardar(img)
    rc, out = afx('repair')
    fin = cargar()
    bien = rc == rc_esperado and (not comparar or fin == protegida) and (extra is None or extra(fin, out))
    caso(f'{nombre} (rc={rc})', bien, out)
    return out


def flip(a, bit=0x40):
    def f(img): img[a] ^= bit
    return f


# 1. el caso real: /usr de la camara 89a17d3 con 2 bits perdidos
def usr_real(img):
    d = open(os.path.join(REPO, 'volcados', 'camara_89a17d3', 'extend.bin'), 'rb').read()
    img[PART['extend']:PART['extend'] + len(d)] = d
out = probar('CASO REAL camara 89a17d3: 2 bits en /usr', usr_real, 2)
caso('  ... nombra las direcciones reales 0x07595F2 y 0x088A17A', '0x07595F2' in out and '0x088A17A' in out, out)

probar('kernel: el bit real 0x16BD14', flip(0x16BD14), 0)
probar('rootfs: 1 bit', flip(PART['rootfs'] + 0x123457, 0x02), 2)


def borrar_bloque(a):
    def f(img): img[a:a + BLK] = b'\xff' * BLK                # corte de luz durante un refresco
    return f
probar('rootfs: bloque entero borrado -> paridad', borrar_bloque(PART['rootfs'] + 7 * BLK), 2)
probar('usr: bloque con 3 bits -> paridad', lambda i: [flip(PART['extend'] + 5 * BLK + k * 999, 0x10)(i) for k in range(3)], 2)
probar('boot: bloque borrado -> desde la copia', borrar_bloque(PART['boot'] + 20 * BLK), 0)
probar('copia de riscv0: 1 bit', flip(PART['bak'] + 43 * BLK + 0x100, 0x01), 0)
probar('riscv0: bloque borrado -> desde la copia', borrar_bloque(PART['riscv0'] + 3 * BLK), 0)
probar('uboot: 1 bit', flip(PART['uboot'] + 0x2345, 0x08), 0)
probar('paridad 3: bloque borrado -> se recalcula', borrar_bloque(PART['bak'] + 57 * BLK), 0)
def private_pierde_carga(img):
    # una celda que pierde carga pasa de 0 a 1: buscar un bit en 0 dentro de private y ponerlo en 1
    for a in range(PART['private'], PART['private'] + BLK):
        if img[a] != 0xFF:
            k = next(b for b in range(8) if not (img[a] >> b) & 1)
            img[a] |= 1 << k; return
probar('private: 1 bit 0->1 -> se restaura', private_pierde_carga, 0)


def private_copia_pierde_carga(img):
    c = PART['bak'] + 52 * BLK
    for a in range(c, c + BLK):
        if img[a] != 0xFF:
            k = next(b for b in range(8) if not (img[a] >> b) & 1)
            img[a] |= 1 << k; return
probar('copia de private: 1 bit 0->1 -> se rehace la copia', private_copia_pierde_carga, 0)


def env_flip(img): img[PART['env'] + 0x200] ^= 0x04          # el env queda con CRC invalido
probar('env: 1 bit (CRC invalido) -> se restaura', env_flip, 0)


# cambio legitimo de private (re-emparejar): la copia debe actualizarse, no revertir
def private_legit(img):
    img[PART['private']:PART['private'] + 16] = bytes(range(16))
probar('private: cambio legitimo -> copia actualizada', private_legit, 0, comparar=False,
       extra=lambda fin, out: fin[PART['private']:PART['private'] + 16] == bytes(range(16)) and
       fin[PART['bak'] + 52 * BLK:PART['bak'] + 52 * BLK + 16] == bytes(range(16)))

# dos bloques con varios bits en el MISMO grupo de paridad (i y i+11): no se puede
def doble(img):
    borrar_bloque(PART['rootfs'] + 2 * BLK)(img); borrar_bloque(PART['rootfs'] + 13 * BLK)(img)
probar('2 bloques borrados en el mismo grupo -> NO reparable (rc=3)', doble, 3, comparar=False)

# refresh no debe cambiar ningun dato
guardar(protegida)
rc, out = afx('-v', 'refresh', '300')
caso('refresh 300 bloques: datos identicos', rc == 0 and cargar() == protegida, out)

print(f'\n{ok_total} OK, {len(fallos)} fallas')
shutil.rmtree(tmp, ignore_errors=True)
sys.exit(1 if fallos else 0)
