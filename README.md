# Reparar Cámara 2026 — BM Pro 2 (Allwinner V821)

Investigación, reparación y **auto-reparación** de las cámaras del baby monitor BM Pro 2 que "mueren de la nada"
(quedan en bucle de reinicio hasta reflashearlas).

> **Resumen en una línea:** la flash SPI pierde **un solo bit** (una celda NOR pierde carga, 0→1).
> Se repara reescribiendo solo el bloque dañado, y se **previene** instalando una copia del kernel + un script
> en U-Boot y otro en Linux que detectan el bit perdido y la cámara se repara sola. Todo validado en hardware.

- Manual de uso del programa para técnicos: **[MANUAL.md](MANUAL.md)**
- Este README es la referencia técnica y la **bitácora de pruebas**: antes de probar algo, buscar aquí si ya se hizo.

---

## Índice

1. [Estructura del repositorio](#1-estructura-del-repositorio)
2. [Hardware y firmware](#2-hardware-y-firmware)
3. [La falla](#3-la-falla)
4. [Reparación manual (histórico placa 2)](#4-reparación-manual-histórico-placa-2)
5. [Auto-reparación: diseño final](#5-auto-reparación-diseño-final)
6. [Programa ReparadorV821.exe](#6-programa-reparadorv821exe)
7. [Lo que sabemos del U-Boot (probado)](#7-lo-que-sabemos-del-u-boot-probado)
8. [Bitácora de pruebas](#8-bitácora-de-pruebas)
9. [Callejones sin salida (NO repetir)](#9-callejones-sin-salida-no-repetir)
10. [Rescate de una cámara](#10-rescate-de-una-cámara)
11. [Pendientes y limitaciones](#11-pendientes-y-limitaciones)
12. [Valores de referencia](#12-valores-de-referencia)

---

## 1. Estructura del repositorio

```
Reparar Camara 2026/
├── README.md                   este documento (referencia técnica + bitácora)
├── MANUAL.md                   manual de uso del .exe
├── herramienta/
│   ├── src/Program.cs          código del ReparadorV821.exe (C# 5, WinForms)
│   ├── linux/S00autofix        script de auto-reparación que se instala en la cámara
│   ├── afx/afx.c               corrección de 1 bit + paridad (se compila para la cámara con Zig)
│   ├── build.ps1               compila el .exe (csc de .NET Framework 4, viene con Windows)
│   └── ReparadorV821.exe       (generado, no se sube)
├── pruebas/
│   ├── simulador_uboot.py      simula el script de U-Boot sobre una imagen de flash
│   ├── test_afx.py             20 pruebas de afx sobre la imagen real (corre afx_pc.exe)
│   ├── armar_imagen.py         arma una imagen de 16 MB desde particiones sueltas
│   ├── xfel_falso.cs           xfel falso que lee/escribe un .bin (probar el .exe sin cámara)
│   ├── uart_registrador.py     registra la UART y permite mandar comandos / cortar autoboot
│   └── uart_enviar.sh          manda comandos por la UART y muestra la respuesta
├── logs/uart/                  logs de la UART (placas 1, 2 y placa de prueba)
├── informe/                    informe .docx ES/EN del 2026-11-20 (diagnóstico inicial)
├── firmware/v0.1.5_extraido/   (no se sube) firmware extraído del .img
├── volcados/                   (no se sube) volcados de flash: placa2/, placa_prueba/
└── vendor/                     (no se sube) xfel 1.4.0 + zadig 2.9, adb (platform-tools), zig 0.17.1
```

**Qué NO se sube a GitHub (`.gitignore`) y por qué:**

| Carpeta | Motivo |
|---|---|
| `firmware/` | Firmware propietario del fabricante. |
| `volcados/` | Incluyen la partición `private` de cada cámara (emparejamiento con el monitor, `ldauth`, licencias `uvlic`): datos únicos de cada equipo. |
| `vendor/` | Binarios de terceros. Se bajan de: xfel → https://github.com/xboot/xfel/releases (v1.4.0, trae `Drivers/zadig-2.9.exe`); adb → Android SDK Platform-Tools (https://developer.android.com/tools/releases/platform-tools). |
| `*.exe` | El .exe embebe el firmware y los binarios de terceros. Se genera con `herramienta/build.ps1`. |

### Compilar el .exe en otro PC

Al clonar el repo **no vienen** el firmware ni los binarios de terceros (están en `.gitignore`), así que hay que
**copiarlos a mano** desde este PC (o un respaldo) antes de compilar:

1. Clonar el repo.
2. Copiar a mano estas dos carpetas a la raíz del repo, con la misma estructura:
   - `firmware/v0.1.5_extraido/` → como mínimo `boot.fex`, `amp_rv0.fex`, `rootfs_nor.fex`, `usr.fex`, `env.fex`
   - `vendor/` → `xfel-windows-v1.4.0/` (`xfel.exe`, `libusb-1.0.dll`, `Drivers/zadig-2.9.exe`) y
     `platform-tools/` (`adb.exe`, `AdbWinApi.dll`, `AdbWinUsbApi.dll`). Si no hay copia, se bajan de las páginas
     oficiales indicadas en la tabla de arriba.
3. Compilar: `powershell -File herramienta\build.ps1`. Usa el `csc` de .NET Framework 4, que ya viene con Windows,
   así que no hay que instalar nada. Genera `herramienta\ReparadorV821.exe`.

**Saltos de línea:** el script que se instala en la cámara (`herramienta/linux/S00autofix`) corre en Linux y
tiene que tener saltos de línea **LF**. Si tuviera CRLF, fallaría en la cámara (`#!/bin/sh\r`). El `.gitattributes`
lo fija en LF (también los `.sh` y `.py`), así que **funciona aunque el repo se clone en Windows** con
`core.autocrlf=true`. Si alguna vez se edita ese archivo con un editor de Windows, comprobar que siga en LF
(`git ls-files --eol herramienta/linux/S00autofix` debe decir `i/lf w/lf`).

---

## 2. Hardware y firmware

| Dato | Valor |
|---|---|
| SoC | Allwinner **V821** (sun300iw1p1), RISC-V rv32imafdc, 960 MHz |
| RAM | 64 MB DDR2 (520 MHz) |
| Flash | SPI NOR **BY25Q128** (Boya) 16 MB, borrado 64 KB, sin ECC |
| Sensor | GC1084 MIPI 1280x720 @ 20 fps |
| Software | BOOT0 → OpenSBI → U-Boot 2018.07-ge71a924 (16-may-2025) → Linux 5.4.220 (Tina, BusyBox 1.33.2) |
| App | `lindipcam` (09e04e0, 2026-03-26) |
| Firmware | `DY_TX_BM188B_v0.1.5_20260630.img` (IMAGEWTY de PhoenixSuit; extraído con un parser propio) |
| Enlace con el monitor | RF / WiFi P2P propio (sin IP por WiFi: normal) |
| UART | 115200 8N1 (adaptador CH340 en COM15) |
| USB | Encendida: **ADB root** ("Tina ADB", 1F3A:0000, driver WinUSB que Windows pone solo). FEL: 1F3A:EFE8 |

### Mapa de la flash

| mtd | Partición | Inicio | Tamaño | Contenido |
|---|---|---|---|---|
| 0 | uboot | 0x000000 | 0x050000 | BOOT0 + boot_package (OpenSBI, U-Boot) |
| 1 | **private** | 0x050000 | 0x010000 | Datos únicos (emparejamiento, licencias). **Nunca sobrescribir.** |
| 2 | env | 0x060000 | 0x010000 | Entorno U-Boot (CRC32 + `var=valor\0`) |
| 3 | boot | 0x070000 | 0x2B0000 | `boot.fex` (0x2A3000): imagen Android, kernel LZMA + DTB al final (0x294000) |
| 4 | riscv0 | 0x320000 | 0x090000 | `amp_rv0.fex` (ELF 0x8FE1C) del coprocesador; la cola de la partición tiene datos ajenos (se deja igual) |
| 5 | rootfs | 0x3B0000 | 0x360000 | squashfs 0x2E0000 (`/`) |
| 6 | extend | 0x710000 | 0x410000 | squashfs `usr.fex` 0x280000 (`/usr`), ranura A de las OTA |
| 7 | extend_backup | 0xB20000 | 0x410000 | Ranura B de las OTA. **Vacía de fábrica → aquí va la copia de auto-reparación** |
| 8 | rootfs_data | 0xF30000 | 0x060000 | jffs2, overlay escribible de `/` (aquí queda `/etc/init.d/S00autofix`) |
| 9 | UDISK | 0xF90000 | 0x070000 | jffs2 `/mnt/UDISK` (aquí queda `autofix.log`) |

Arranque: `bootcmd = run setargs_nor boot_normal`; `boot_normal` original = `sunxi_flash read 82000000 ${boot_partition};bootm 82000000` (sin verificar nada).
Linux: `/init` monta el overlay → `rcS` → `rc.final` ejecuta `/etc/init.d/S??*` en orden alfabético (`S00mpp` carga riscv0 al coprocesador, `S09app` lanza `lindipcam`).

**OTA:** existe un sistema A/B (`/usr/bin/start_ota` → `/sbin/otaswupdate` → `swupdate`), que escribe la ranura contraria
(`extend_partition=extendA` → escribe `extend_backup` y pasa a `extendB`). Ninguna cámara vista tenía OTA hecha.

---

## 3. La falla

- **Síntoma:** bucle de reinicio cada ~1,2 s. UART: BOOT0 → OpenSBI → U-Boot OK → `Detect comp lzma` → `lzma compressed: uncompress error 1` → `Must RESET board to recover`.
- **Causa confirmada (placa 2, 2026-11-20):** volcado completo por FEL (xfel) comparado con `boot.fex`: **1 solo bit distinto** en toda la flash:
  `0x16BD14` (boot+0xFBD14): `0x0B → 0x4B` (bit 6, 0→1). Una celda NOR que perdió carga (retención). Todo lo demás idéntico.
- Es la misma falla en todas las cámaras muertas vistas. Que pase en muchas unidades apunta a un **lote de flash débil o a una grabación de fábrica marginal** (vale la pena reclamar al proveedor).
- **Segundo modo de falla (descubierto 2026-11-23):** un bit perdido en **riscv0** → `Unable to handle kernel NULL pointer dereference` → `Kernel panic` a los ~3 s al cargar el coprocesador (`Load mpp modules`) → también bucle. Lo cubre la auto-reparación.
- **Tercer modo de falla (cámara real `2c000c3c140089a17d3`, 2026-11-23):** **2 bits perdidos en `/usr`** (partición `extend`, squashfs), ambos 0→1:
  `0x07595F2: 0xD4→0xF4` y `0x088A17A: 0x56→0x5E`. El kernel arranca, pero dmesg muestra
  `SQUASHFS error: Unable to read fragment cache entry [3432e]` → la app no puede leer sus archivos → deja de
  alimentar el watchdog (16 s) → **reinicio cada ~20 s**. Hay ADB durante ~15 s de cada ciclo: alcanza para
  repararla con el .exe en modo ADB (lo hace en ~10 s). La auto-reparación **no** cubre `/usr` ni `rootfs` (ver §11).

| Modo | Dónde | Síntoma | ¿Se repara sola con la protección? | ¿La repara el .exe? |
|---|---|---|---|---|
| 1 | kernel (`boot`) | bucle a ~1 s, `uncompress error` (sin Linux, sin ADB) | **Sí** (U-Boot arranca con la copia, afx reescribe) | Sí (FEL) |
| 2 | `riscv0` | kernel panic a ~3 s, `Load mpp modules` | **Sí** (afx antes de `S00mpp`) | Sí |
| 3 | `/usr` (`extend`) o `rootfs` | arranca, errores SQUASHFS, reinicio por watchdog a ~20 s (ADB unos 15 s); si cae en `/init` o busybox: panic a ~1,3 s | **Sí** (U-Boot lo ve por CRC → modo rescate → afx con paridad) | Sí (ADB en la ventana, o FEL) |

Con la protección completa (env v4 + S00autofix v6 + afx) **los tres modos se reparan solos**: validado en hardware con los
tres a la vez (prueba 39).

---

## 4. Reparación manual (histórico placa 2)

1. Driver WinUSB sobre 1F3A:EFE8 con Zadig (reemplaza el de PhoenixSuit).
2. `xfel ddr` y luego `xfel spinor read 0 16777216 volcado.bin`.
3. Comparar con `firmware/v0.1.5_extraido/boot.fex` en 0x70000.
4. Corregir y escribir **solo el bloque de 64 KB** dañado (`xfel spinor write 0x160000 bloque.bin`).
5. Resultado: arranca hasta `boot complete` y **sigue emparejada** con el monitor, porque `private` no se tocó.

> Reflashear todo con PhoenixSuit no hace falta y arriesga `private`. El `.exe` automatiza esta reparación.

---

## 5. Auto-reparación: diseño final

Tres capas, porque **U-Boot no puede escribir la flash** en este firmware (ver §7): U-Boot **detecta** y arranca algo que
funcione; Linux/afx **reparan**.

### Capa 1 — U-Boot (env, `autofix_ver=4`)

En cada arranque verifica el CRC32 de **rootfs, usr y kernel**:
- **kernel** dañado y copia sana → **arranca con la copia cargada en RAM**;
- **rootfs o usr** dañados → **modo rescate**: arranca el kernel con `root=/dev/mtdblock8 rootfstype=jffs2 rw`
  (`rootfs_data`, donde vive afx) e `init=/upper/etc/autofix/afx`. afx corre como proceso 1 (estático: no necesita nada
  de rootfs), crea `/dev`, repara todo con la paridad y las copias, y reinicia. Freno: `af_rc` cuenta los intentos con
  `saveenv` (máximo 3; `S00autofix` lo borra al arrancar normal).

```
boot_normal  = sunxi_flash read 82000000 ${boot_partition};run af_r;run af_k;bootm 82000000
af_r         = setenv af_bad;run af_s_${extend_partition} af_rr af_ru;run af_resc_${af_bad}
af_rr        = sunxi_flash read 82000000 rootfs 2E0000;crc32 82000000 2E0000 81F00000;setenv af_x setenv af_bad 1;run af_rrchk af_xok;run af_x
af_rrchk     = itest.l *81F00000 == dff95cd2
af_ru        = sunxi_flash read 82000000 extend 280000;crc32 82000000 280000 81F00000;setenv af_x setenv af_bad 1;run af_ruchk af_xok;run af_x
af_ruchk     = itest.l *81F00000 == bb034641
af_xok       = setenv af_x itest 1 == 1
af_resc_     = itest 1 == 1
af_resc_1    = echo [autofix] ROOTFS/USR DANADO;run af_cnt_${af_rc} af_rgo
af_cnt_      = setenv af_rc 1;saveenv          (af_cnt_1 -> 2, af_cnt_2 -> 3; af_cnt_3 no existe = basta)
af_rgo       = echo [autofix] MODO RESCATE: afx repara con la paridad;setenv nor_root /dev/mtdblock8;setenv init /upper/etc/autofix/afx rootfstype=jffs2 rw;run setargs_nor
af_k         = sunxi_flash read 82000000 boot 2A3000;crc32 82000000 2A3000 81F00000;setenv af_kx run af_kfix;run af_kchk af_kok;run af_kx
af_kchk      = itest.l *81F00000 == 75ca6eb5
af_kok       = setenv af_kx echo [autofix] kernel OK
af_s_extendA = itest 1 == 1
af_kfix      = echo [autofix] KERNEL DANADO, verificando copia;sunxi_flash read 82000000 extend_backup 2B0000;crc32 82000000 2A3000 81F00000;setenv af_kw run af_kbad;run af_s_${extend_partition} af_kchk af_kgo;run af_kw
af_kgo       = setenv af_kw echo [autofix] copia OK: arrancando con la copia (Linux repara la flash)
af_kbad      = echo [autofix] copia no valida: arranque normal;sunxi_flash read 82000000 boot
boot_normal_orig = (el boot_normal original, guardado)
```

- "si/si no" sin `if`: se carga un valor por defecto (`setenv X ...`) y `run cond accion` lo cambia solo si `cond` sale bien.
- `boot_normal` **tiene que empezar igual que el original**: U-Boot lo lee al arrancar para cargar el DTB del kernel.
- La copia y el rescate solo se usan con la ranura OTA `extendA` (`af_s_${extend_partition}` no existe para `extendB`).
- **Si algo no cuadra, nunca escribe la flash y arranca igual que el original** (falla segura, comprobada en hardware).
  Lo único que escribe es el `env` (`saveenv` del contador de rescates).
- **`rw` es imprescindible** en el arranque de rescate: sin él, el kernel monta la raíz en solo lectura, afx no puede crear
  `/dev` y no ve la flash (prueba 35). afx además remonta en escritura y crea los nodos `/dev/mtdN` con `mknod` si hace falta.
- Costo: el arranque normal pasa de ~12 s a ~19 s (U-Boot lee y verifica ~8 MB, y afx revisa toda la flash al arrancar).

### Capa 2 — Linux (`/etc/init.d/S00autofix`, v6)

Está en `herramienta/linux/S00autofix` y se instala en el overlay persistente (`rootfs_data`):

- Corre **antes que `S00mpp`** (orden alfabético). Si afx está instalado, ejecuta `afx repair` (capa 3) y reinicia si reparó
  rootfs/usr (freno: 3 reinicios seguidos); deja corriendo el refresco cada 6 h. Si venía del modo rescate, pasa
  `/etc/autofix/rescue.log` al registro y borra `af_rc`.
- Sin afx (instalaciones viejas) hace lo de la v4: revisa `riscv0` contra su copia y lo repara **antes de que se cargue**;
  en segundo plano (15 s después) compara el kernel con el MD5 de referencia y reescribe la flash desde la copia.
- Guardas: solo actúa si `extend_partition=extendA`, si no hay `swu_mode` (OTA) y si la copia empieza por `ANDROID!` (es nuestra y no una OTA).
- Registro en `/mnt/UDISK/autofix.log` y en la consola (`[autofix] ...`).

### Capa 3 — afx (`/etc/autofix/afx`): corrección de 1 bit + paridad para TODA la flash fija

Programa en C (`herramienta/afx/afx.c`, compilado estático para RISC-V con Zig), lo llama `S00autofix` v5 al arrancar
(`afx repair`) y cada 6 h (`afx refresh 2` + `afx repair`). Manifiesto en `/etc/autofix/manifest` (CRC32 + MD5 de cada
bloque de 64 KB, generado con `afx init` cuando la flash está verificada contra el firmware).

| Zona | Cómo la repara afx |
|---|---|
| `rootfs` (46 blq) y `/usr` (40 blq) | **1 bit**: el CRC ubica el bit exacto al instante (síndrome lineal del CRC32, sin probar posiciones). **Varios bits o bloque borrado**: paridad XOR (11 grupos de ≤8 bloques, guardada en `extend_backup` 54-64) |
| `boot`, `riscv0` y sus copias | 1 bit, o desde la otra copia (bloque a bloque) |
| `private` | Copia en `extend_backup` 52. Si difiere en **1 bit 0→1** = daño → se restaura; si no = cambio legítimo (re-emparejar) → se actualiza la copia |
| `env` | Copia en `extend_backup` 53. Si su CRC es inválido → se restaura; si es válido y distinto → se actualiza la copia |
| `uboot` (boot0) | Solo corrección de 1 bit (puede cambiar legítimamente: nunca se restaura entero ni se refresca) |
| Paridad | 1 bit, o se recalcula desde los datos sanos |

- **Refresco preventivo:** reescribe 2 bloques cada 6 h con sus mismos datos (toda la flash en ~26 días) para recargar las
  celdas antes de que pierdan un bit. Un corte de luz a mitad de un refresco deja un bloque borrado, y la paridad lo rehace.
- Si reparó `rootfs` o `/usr` (montados), `S00autofix` reinicia (freno: máximo 3 reinicios seguidos).
- Si el daño impide arrancar Linux (por ejemplo, en `/init`), afx no llegaría a correr desde `S00autofix` (prueba 30): para eso
  está el **modo rescate** de la capa 1, que arranca afx directamente como proceso 1 (pruebas 34-39).
- Códigos de salida: 0 OK, 1 error, 2 reparado y hace falta reiniciar, 3 daños no reparables, 4 (`check`) daños reparables.
- Pruebas en el PC: `python pruebas/test_afx.py` (20 casos sobre la imagen real, incluido el caso de la cámara 89a17d3).
- Compilar: `vendor\zig\zig.exe cc -target riscv32-linux-musl -static -O2 -s -o herramienta\afx\afx herramienta\afx\afx.c`
  (y `zig cc -O2 -o afx_pc.exe afx.c` para las pruebas en el PC). Zig 0.17.1 portable: https://ziglang.org/download/

### Copia en `extend_backup`

| Offset en extend_backup | Tamaño | Contenido |
|---|---|---|
| 0x000000 | 0x2B0000 | partición `boot` completa (boot.fex + relleno 0xFF) |
| 0x2B0000 | 0x090000 | partición `riscv0` de esa misma cámara (incluida su cola) |
| 0x340000 | 0x010000 | (afx) copia de `private` |
| 0x350000 | 0x010000 | (afx) copia del `env` |
| 0x360000 | 0x0B0000 | (afx) 11 bloques de paridad de rootfs + usr |

---

## 6. Programa ReparadorV821.exe

Manual de uso: **[MANUAL.md](MANUAL.md)**. Aspectos técnicos:

- Un solo .exe (~26 MB) que embebe xfel, zadig, adb, el firmware v0.1.5 y `S00autofix`. Los extrae en `%LOCALAPPDATA%\ReparadorV821\`.
- **Elige el modo solo:** si hay una cámara encendida por ADB usa ADB; si no, FEL (xfel).
- Lee `private`, `env`, `boot`, `riscv0`, `rootfs`, `extend` y la copia; compara por bloques de 64 KB contra el firmware; repara solo los bloques distintos; instala la copia, el env (fusionando con el env actual de la cámara: conserva sus variables y borra las `af_*` viejas) y el script; **relee todo para verificar**.
- No escribe nada si: hay más de 64 bits distintos (otro firmware), `extend_backup` tiene datos desconocidos, o la cámara tiene una OTA hecha o pendiente.
- Registros y respaldos por cámara en `Documentos\ReparadorCamarasV821\<fecha>_<serie>\`.
- Compilar: `powershell -File herramienta\build.ps1 [-Out nombre.exe]`.
- Argumentos ocultos para pruebas sin clics: `--auto diag|repair|simk|simr`, `--test`, `--out <log.txt>`, `--outroot <carpeta>`, `--xfel <exe>`, `--adb <exe>`.
  Con `pruebas/xfel_falso.cs` (compilar con `csc`) y la variable `FAKE_XFEL_IMG=<imagen.bin>` se prueba el modo FEL contra una imagen en el PC.

---

## 7. Lo que sabemos del U-Boot (probado)

U-Boot 2018.07-ge71a924. Probado en la consola de la placa de prueba (con `bootdelay=2`) el 2026-11-23.

| Qué | Resultado |
|---|---|
| Comandos disponibles (`help`) | base bdinfo boot bootd bootm bootr cmp coninfo cp crc32 echo editenv efex env erase fat* fdt flinfo itest load* loop md mm mmc mw nm part printenv protect reset run saveenv set_working_fdt setenv sf sunxi_card0_probe sunxi_cmd_parser sunxi_flash sunxi_mem_info_dump timer_* version |
| `if` / `&&` (hush) | **No existe**: intérprete simple |
| `setexpr` | **No existe** (aparece en los strings del binario, pero no está compilado) |
| `crc32 a l dst` | OK; guarda el CRC como palabra **nativa** (`md.l` muestra el mismo valor que imprime) |
| `itest.l *addr == valor` | OK |
| `run A B` | OK: B corre solo si A salió bien (se usa como condicional) |
| `sunxi_flash read 82000000 <part> <tam>` | OK, tamaño en **bytes hex** |
| Cargar en otra dirección (p. ej. 0x83000000) | **Rechazado** (`exceed boot scope`, deja ceros): usar siempre 0x82000000 |
| `sunxi_flash write` (todas las variantes) | **No escribe**: solo muestra la ayuda (ver §9) |
| `sf probe` | Falla (`Failed to initialize SPI flash at 0:0`) |
| `saveenv` | OK (lo único que U-Boot escribe) |
| Cambiar el inicio de `boot_normal` | **Rompe** el arranque: `ERROR: attempting read past flash size`, `FDT_ERR_BADMAGIC`, no carga el DTB → Linux se cuelga tras `earlycon` |
| `bootdelay=2` | Permite entrar a la consola por la UART (con 0 no da tiempo) |
| Botón de la placa | Lo lee **U-Boot** (no el BROM) en `sunxi_ext_gpio_ctrl`: imprime `FEL key detecting ...!` cada ~50 ms durante ~3 s. **Mantenido apretado** → `Start up with extflag=2` (modo test de fábrica, `lindipcam -t`). **5 pulsaciones** (pulsar y soltar) dentro de esa ventana → `FEL key get 1..5 times !` → `FEL mode confirmed` → **FEL del BROM** (xfel responde `AWUSBFEX ID=0x00188200(V821)`). **Validado 2026-11-23.** Tiene que estar el **USB conectado al PC** en ese momento (si no, se reinicia y arranca normal). Funciona en las cámaras muertas por esta falla porque U-Boot está sano. En FEL el driver es el de PhoenixSuit (`usbUDisc`): para xfel hay que poner WinUSB con Zadig |

---

## 8. Bitácora de pruebas

### 2026-11-20 — Diagnóstico (placas 1 y 2)

| # | Prueba | Resultado |
|---|---|---|
| 1 | UART de placa 2 muerta | `uncompress error 1` en el LZMA del kernel → bucle (`logs/uart/... Bad.log`) |
| 2 | Volcado por FEL (xfel) ×2 | Idénticos entre sí; vs firmware: 1 bit en 0x16BD14 |
| 3 | Escribir solo el bloque 0x160000 | Arranca (`... Reparada.log`) y **funciona con el monitor** (emparejamiento conservado) |
| 4 | Mensajes `tunnel_adapter_create((null)) failed`, `jpeg input orig frame list ... empty`, sin IP | **Normales** en este firmware (también en la placa sana) |

### 2026-11-23 — Auto-reparación (placa de prueba, serie `2c000c3c14008782813`, sana, v0.1.5)

Log completo: `logs/uart/COM15_2026-11-23_pruebas_autofix.log`.

| # | Prueba | Resultado |
|---|---|---|
| 1 | Análisis del firmware: `extend_backup` vacía, comandos de U-Boot por strings | Hay espacio para la copia (cuidado: los strings mienten, ver `setexpr`) |
| 2 | .exe v1 en seco (xfel falso + simulador) sobre el volcado de placa 2 | Detecta el bit real, repara y protege; `private` intacto |
| 3 | Entrar en FEL con el botón **mantenido apretado** | **No entra**: U-Boot arranca con `extflag=2` (ver §7). Lo que funciona son **varias pulsaciones** (dato del usuario, así flashea con PhoenixSuit) |
| 4 | Cámara encendida por USB | **ADB con root**; particiones `/dev/mtdblockN` escribibles; hay `dd`, `md5sum`, `fw_printenv/fw_setenv` |
| 5 | Respaldo completo por ADB (`dd` a `/tmp` + `adb pull`; no hay `exec-out`) | OK, MD5 iguales (`volcados/placa_prueba/`) |
| 6 | Script v1 (con `setexpr`) instalado por ADB | `setexpr` no existe → **falla segura** (no escribe y arranca). Pero cambiar `boot_normal` rompió la carga del DTB → **kernel colgado** |
| 7 | Rescate desde la consola de U-Boot | Restaurar el prefijo de `boot_normal` + `saveenv` → arranca bien |
| 8 | Pruebas en la consola (§7) | No hay hush ni setexpr; `crc32` nativo; `itest *addr` y `run A B` sirven |
| 9 | Script v2 (`itest`), arranque sano | `[autofix] kernel OK` |
| 10 | v2 + bit real dañado por ADB | Detecta, verifica la copia y **arranca con la copia**; la escritura a flash falla (ayuda de `sunxi_flash`) |
| 11 | Variantes de `sunxi_flash write` / `write_mtd` / `sf` en consola | Ninguna escribe (§9) |
| 12 | Capa Linux `S99autofix` | **Kernel reparado en la flash** (MD5 original). Error en el offset de la copia de riscv0 (720896 → 704512): corregido |
| 13 | Bit dañado en riscv0 (+ kernel) | **Kernel panic** en `S00mpp`, antes de `S99autofix` → bucle |
| 14 | Rescate: consola U-Boot → `setenv init /bin/sh; run bootcmd` → `dd` desde la copia | Reparado. Luego la consola se inunda de `awbase abnormal` → cortar energía |
| 15 | Script renombrado a `S00autofix` (riscv0 al instante, kernel en segundo plano) + kernel y riscv0 dañados | U-Boot arranca con la copia; riscv0 reparado antes de cargarse; kernel reparado; siguiente arranque `kernel OK` |
| 16 | Copia dañada (kernel y riscv0) con el original sano | Linux **rehace la copia** |
| 17 | .exe modo ADB: diagnóstico | 6 s, todo detectado |
| 18 | .exe modo ADB: "Simular falla kernel" | Daña, reinicia, se repara sola; diagnóstico OK |
| 19 | Cámara a estado de fábrica → .exe "Reparar y proteger" (modo normal) | Instalado y verificado en **13 s**; arranque `bootdelay=0`, `kernel OK`, `boot complete` a los 12 s (sin demora) |

### 2026-11-23 — Primera cámara real reparada con el .exe (serie `2c000c3c140089a17d3`)

Registros: `Documentos\ReparadorCamarasV821\20261123_115810_...` (reparación) y `..._115833_...` (diagnóstico posterior).
Volcado de la `extend` dañada: `volcados/camara_89a17d3/extend.bin` (MD5 `5050a36d6e8704e9cd76a12a2646973c`).

| # | Prueba | Resultado |
|---|---|---|
| 20 | Conectar la cámara "mala" por USB | **Aparece por ADB** a intervalos: no es el modo 1. Kernel, riscv0, rootfs y env sanos (MD5) |
| 21 | Comparar `extend` con `usr.fex` | **2 bits perdidos** (0→1) en 2 bloques; dmesg: `SQUASHFS error ... [3432e]`; reinicio por watchdog cada ~20 s |
| 22 | .exe "Reparar y proteger" (modo ADB) dentro de la ventana | **Una sola pasada, 10 s**: 2 bloques reescritos + copia + env + script, todo verificado |
| 23 | Después | Encendida >100 s sin reiniciarse, 0 errores SQUASHFS, `/usr` idéntico al original; diagnóstico "Camara sana y protegida" |

### 2026-11-23 — afx (corrección de 1 bit + paridad) y modo FEL en hardware (placa de prueba)

Log UART (pruebas 24-40): `logs/uart/COM15_2026-11-23_afx_y_fel.log`.

| # | Prueba | Resultado |
|---|---|---|
| 24 | Compilar afx para la cámara con Zig 0.17.1 (`riscv32-linux-musl`, estático) | ELF32 RISC-V, flags 0x5 (RVC + float doble), 103 KB; arranca en la cámara |
| 25 | `pruebas/test_afx.py` en el PC (20 casos sobre la imagen real) | **20/20 OK**, incluido el caso real de la cámara 89a17d3 |
| 26 | `afx init` en la placa (simulación y real) | 4 s; 210 bloques protegidos; manifiesto = firmware original bloque a bloque (137/137) |
| 27 | Inyectar los 2 bloques reales de `/usr` de la cámara 89a17d3 → `afx repair` | **Corregidos los 2 bits** (0x07595F2 bit 5, 0x088A17A bit 3); `/usr` = MD5 original |
| 28 | Hallazgo: `mtdblock` deja en RAM el último bloque escrito de una partición **montada**; `sync` no siempre lo baja | `afx` escribe con `fsync` y verifica con `/dev/mtdN` crudo (OK). El .exe en ADB usaba `dd`+`sync`: pasar a `conv=fsync` y verificar en crudo (pendiente) |
| 29 | `S00autofix` v5 (llama a afx; reinicia si reparó rootfs/usr; refresco 2 bloques/6 h) — arranque sano | `boot complete` a los 16 s (antes 12: +4 s de revisión), sin bucles |
| 30 | 4 daños a la vez (bit real del kernel + riscv0 + 2 bloques reales de /usr + **un bloque de rootfs borrado**) y reinicio | U-Boot arranca con la copia, pero **`Kernel panic: Requested init /init failed (error -5)`**: el bloque borrado contenía parte de `/init` → Linux no arranca y afx no llega a correr. **Límite de la capa Linux** (ver §11) |
| 31 | Rescate con `bootdelay=0` mandando Enter sin parar por la UART | No interrumpe U-Boot |
| 32 | **FEL con 5 pulsaciones** (USB conectado) + Zadig (WinUSB) + xfel | `FEL mode confirmed` → xfel: `AWUSBFEX ID=0x00188200(V821)`. Sin el USB conectado se reinicia y arranca normal |
| 33 | .exe en **modo FEL**: diagnóstico + reparación de los 4 daños (con el nuevo criterio por bloques) | 4 zonas detectadas, **5 bloques reescritos en 31 s**; arranca: `kernel OK`, `boot complete` 12,9 s, MD5 de kernel/riscv0/rootfs/usr = originales, `afx check` todo OK |

### 2026-11-23 — Modo rescate e integración de afx en el .exe (placa de prueba)

| # | Prueba | Resultado |
|---|---|---|
| 34 | env v4 (CRC de rootfs/usr en U-Boot) en el simulador y en la placa, arranque sano | U-Boot calcula `dff95cd2` (rootfs), `bb034641` (usr), `75ca6eb5` (kernel); `kernel OK`; arranca normal |
| 35 | Borrar el bloque 7 de rootfs (`/init`) → reinicio (primera versión, sin `rw`) | U-Boot: `ROOTFS/USR DANADO` → `saveenv` → **modo rescate**; el kernel monta la jffs2 y **arranca afx como proceso 1**, pero afx no ve la flash (`ERROR leyendo boot 0`): raíz en solo lectura → sin `/dev`. Freno: tras 3 intentos (`af_cnt_3 not defined`) deja de intentar |
| 36 | Rescate con `init=/bin/sh` | Falla: `/bin/sh` también estaba en el bloque borrado (`error -5`) |
| 37 | Rescate manual desde la consola de U-Boot agregando `rw` | afx: **`rootfs bloque 7 -> reconstruido con la paridad`**, reinicia; arranque normal, `S00autofix` v6 anota el rescate y borra `af_rc` |
| 38 | .exe integrado (modo ADB): actualiza afx, escribe el env v4 (con `rw`), `afx init` + `afx check` | 20 s, todo verificado |
| 39 | **4 daños a la vez** (bit real del kernel + riscv0 + 2 bloques reales de /usr + bloque de rootfs borrado) y reinicio, **sin intervenir** | U-Boot → rescate (kernel desde la copia); afx repara **6 zonas** en ~5 s (1 bit en kernel, riscv0 y usr ×2; rootfs con la paridad; copia del env) y reinicia; arranque normal. MD5 = originales, `afx check` OK |
| 40 | .exe en modo normal (bootdelay=0) + diagnóstico | "Camara sana y protegida", afx OK; `boot complete` a los ~19 s |

---

## 9. Callejones sin salida (NO repetir)

| Intento | Por qué no sirve |
|---|---|
| `setexpr.l var *addr` en U-Boot | El comando no existe → `Unknown command 'setexpr.l'` |
| `if ...; then ...; fi`, `&&` | No hay hush |
| `sunxi_flash write 82000000 boot 0 2B0000` | Muestra la ayuda, no escribe |
| `sunxi_flash write 82000000 boot 2B0000` | Ídem |
| `sunxi_flash write 82000000 boot` | Ídem |
| `sunxi_flash write 0x82000000 boot 0x2B0000` | Ídem |
| `sunxi_flash write_mtd 82000000 boot` | Ídem |
| `sf probe` + `sf write/update` | `Failed to initialize SPI flash at 0:0` |
| `sunxi_flash read 83000000 ...` (otra dirección) | `exceed boot scope`: no carga (quedan ceros) |
| `boot_normal = run ...;bootm` (sin el `sunxi_flash read ... ${boot_partition}` al inicio) | U-Boot no encuentra el DTB → Linux colgado |
| Verificar riscv0 en U-Boot | Inútil: riscv0 lo carga Linux desde la flash (se quitó del script) |
| Script Linux como `S99*` | Corre después de `S00mpp`: un riscv0 dañado ya provocó el panic |
| Arranque de rescate sin `rw` en los bootargs | La raíz jffs2 queda en solo lectura: afx no puede crear `/dev` ni ver la flash (prueba 35) |
| `init=/bin/sh` con `rootfs` dañado | `/bin/sh` (busybox) puede estar en el bloque dañado: `Requested init /bin/sh failed (error -5)` |
| Verificar escrituras releyendo `/dev/mtdblockN` | Puede devolver lo que `mtdblock` tiene en RAM: verificar con `/dev/mtdN` (crudo) |
| Escribir con `dd` + `sync` en una partición montada | El último bloque puede quedar en RAM: usar `dd ... conv=fsync` |
| Probar el .exe "en seco" con la cámara conectada por USB | El .exe elige ADB primero y escribe en la cámara real: para el xfel falso, desconectar la cámara o pasar `--adb` a un ejecutable que no sea adb |
| `adb exec-out` | adbd demasiado viejo: usar `adb shell dd ... of=/tmp/x` + `adb pull` |
| Rutas `/tmp/...` en Git Bash con adb | MSYS las convierte a rutas de Windows: usar `MSYS_NO_PATHCONV=1` |
| Login por UART en Linux | Pide usuario y contraseña (desconocidos): no hace falta, hay ADB root |
| Mantener apretado el botón para FEL | Da `extflag=2` (modo test), no FEL. Hay que **pulsarlo 5 veces** al encender, con el USB conectado |

---

## 10. Rescate de una cámara

| Situación | Cómo |
|---|---|
| Arranca (aunque sea con fallos) | ADB: el .exe en modo ADB, o `adb shell` (root) |
| Cualquier bit perdido, **con la protección completa** (env v4 + S00autofix v6 + afx) | No hace falta nada: se repara sola (kernel desde la copia, rootfs/usr por modo rescate, el resto en Linux) |
| Bucle por el kernel **sin protección** | FEL (pulsar el botón **5 veces** al encender, con el USB conectado) + .exe (o xfel a mano, §4); luego una pasada por ADB para instalar el resto. Plan B: PhoenixSuit + .exe por ADB |
| Rescate a mano desde la consola de U-Boot (`bootdelay=2`) | `setenv nor_root /dev/mtdblock8` → `setenv init /upper/etc/autofix/afx rootfstype=jffs2 rw` → `run setargs_nor` → `run af_k` → `bootm 82000000` |
| Bucle en Linux (panic) con `bootdelay=2` y UART | Consola U-Boot (Enter al ver `Hit any key`) → `setenv init /bin/sh` → `run bootcmd` → shell root sin rcS → `dd` desde la copia (`/dev/mtdblock7`) → cortar energía. Montar `/usr` para `md5sum`: `mount -t squashfs -o ro /dev/mtdblock6 /usr` |
| U-Boot con `bootdelay=0` | No se puede interrumpir (probado: mandar Enter sin parar por la UART no lo detiene): FEL con 5 pulsaciones |

Comandos útiles por ADB (rutas con `MSYS_NO_PATHCONV=1` desde Git Bash):

```sh
adb shell "md5sum /dev/mtdblock3 /dev/mtdblock4"            # kernel y riscv0 (ver §12)
adb shell "cat /mnt/UDISK/autofix.log"                      # que reparo el script
adb shell "fw_printenv autofix_ver bootdelay boot_normal"   # estado del env
adb shell "dd if=/dev/mtdblock3 of=/tmp/p.bin bs=65536" && adb pull /tmp/p.bin boot.bin
```

Desinstalar la protección a mano (dejarla como de fábrica): escribir el env original de esa cámara en `mtdblock2`
(está en su carpeta de registro como `env_antes.bin`), llenar `mtdblock7` con 0xFF y borrar `/etc/init.d/S00autofix`.

---

## 11. Pendientes y limitaciones

- [x] **Modo FEL del .exe**: validado el 2026-11-23 (pruebas 32-33): 5 pulsaciones con el USB conectado.
- [x] **rootfs / usr**: los cubre **afx** (1 bit por CRC + paridad XOR, §5), también cuando el daño impide arrancar Linux,
  gracias al **modo rescate** (pruebas 34-39).
- [x] **Integración de afx en el .exe** (instala, `afx init`, `afx check` en el diagnóstico) y escritura ADB segura
  (`dd conv=fsync` + verificación por `/dev/mtdN`) (prueba 38).
- [ ] **Tiempo de arranque**: pasó de ~12 s a ~19 s. Idea: como U-Boot ya garantiza kernel, rootfs y usr sanos al arrancar,
  `S00autofix` podría revisar en primer plano solo `riscv0` (lo único que se carga antes de la app) y dejar el resto de
  `afx repair` en segundo plano.
- [ ] **Bits en el DTB** (final de `boot`, 0x294000–0x2A3000, ~2 % del kernel): U-Boot carga el DTB desde la partición
  dañada antes del script → Linux se cuelga. Se repara con el .exe (FEL). Idea: usar `set_working_fdt` / `fdt` con el DTB
  de la copia.
- [ ] **uboot (boot0/U-Boot)**: afx corrige 1 bit, pero si el daño impide arrancar U-Boot no hay nada que lo detecte:
  sin U-Boot no hay pulsaciones para FEL. Habría que ver si el BROM entra solo en FEL cuando boot0 es inválido.
- [ ] **Mejora barata:** que la capa U-Boot también se desactive con OTA pendiente (`run af_s_${extend_partition}${swu_mode} ...`).
  Hoy solo mira `extend_partition`. Si alguna vez se usa OTA en estas cámaras, revisar antes.
- [ ] Si la app o un "reset de fábrica" borra el overlay (`rootfs_data`), se pierden `S00autofix` y afx: la capa U-Boot
  sigue arrancando con la copia del kernel, pero el rescate no encontraría afx. El diagnóstico del .exe lo muestra.
- [ ] Confirmar con el monitor que la placa de prueba sigue emparejada después de instalar la protección.
- [ ] Reclamar al proveedor de la flash / fábrica (retención de celdas NOR).

---

## 12. Valores de referencia

| Qué | Valor |
|---|---|
| CRC32 `boot.fex` (0x2A3000) | `75ca6eb5` |
| CRC32 kernel con el bit real perdido | `ab6af357` |
| MD5 partición `boot` sana (0x2B0000) | `d40e8aa522d32bd98e359b4db6843a9c` |
| MD5 `boot` con el bit real perdido | `fa629abffc8dd051c06d1e97ae54e7a0` |
| CRC32 / MD5 `amp_rv0.fex` (0x8FE1C) | `ba77b129` / `bcc2764b9baad5457a5e630c88e0cda9` |
| CRC32 `rootfs_nor.fex` / `usr.fex` / `env.fex` | `dff95cd2` / `bb034641` / `4a86a71e` |
| `Vboot.fex` (sum32 del kernel) | `0xB8278DCC` |
| Bit de la falla real | abs `0x16BD14` (boot+0xFBD14): `0x0B → 0x4B` |
| Inicio de la copia de riscv0 en `extend_backup` | bloque 43 (0x2B0000); en palabras de 4 bytes: 704512 |
| Formato del env | CRC32 (LE) de `[4:0x10000]` + `var=valor\0...\0\0`, relleno con ceros (igual con `saveenv`) |
