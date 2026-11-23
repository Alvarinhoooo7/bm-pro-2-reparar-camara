"""Arma una imagen de flash de 16 MB a partir de particiones sueltas (las que saca el .exe o adb).

Uso:  python armar_imagen.py <carpeta_con_particiones> <salida.bin>
La carpeta debe tener (las que falten quedan en 0xFF):
  private.bin env.bin boot.bin riscv0.bin rootfs.bin extend.bin extend_backup.bin
Sirve para usar el .exe con xfel_falso.exe o con simulador_uboot.py sin tocar una camara.
"""
import os, sys

MAPA = [('uboot', 0x0), ('private', 0x50000), ('env', 0x60000), ('boot', 0x70000), ('riscv0', 0x320000),
        ('rootfs', 0x3B0000), ('extend', 0x710000), ('extend_backup', 0xB20000),
        ('rootfs_data', 0xF30000), ('UDISK', 0xF90000)]

carpeta, salida = sys.argv[1], sys.argv[2]
img = bytearray(b'\xff' * 0x1000000)
for nombre, addr in MAPA:
    p = os.path.join(carpeta, nombre + '.bin')
    if os.path.exists(p):
        d = open(p, 'rb').read(); img[addr:addr + len(d)] = d
        print(f'{nombre:14s} 0x{addr:07X}  {len(d):8d} bytes')
open(salida, 'wb').write(img)
print('imagen:', salida)
