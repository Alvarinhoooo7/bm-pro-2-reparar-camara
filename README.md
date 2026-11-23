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
│   ├── build.ps1               compila el .exe (csc de .NET Framework 4, viene con Windows)
│   └── ReparadorV821.exe       (generado, no se sube)
├── pruebas/
│   ├── simulador_uboot.py      simula el script de U-Boot sobre una imagen de flash
│   ├── armar_imagen.py         arma una imagen de 16 MB desde particiones sueltas
│   ├── xfel_falso.cs           xfel falso que lee/escribe un .bin (probar el .exe sin cámara)
│   ├── uart_registrador.py     registra la UART y permite mandar comandos / cortar autoboot
│   └── uart_enviar.sh          manda comandos por la UART y muestra la respuesta
├── logs/uart/                  logs de la UART (placas 1, 2 y placa de prueba)
├── informe/                    informe .docx ES/EN del 2026-11-20 (diagnóstico inicial)
├── firmware/v0.1.5_extraido/   (no se sube) firmware extraído del .img
├── volcados/                   (no se sube) volcados de flash: placa2/, placa_prueba/
└── vendor/                     (no se sube) xfel 1.4.0 + zadig 2.9, adb (platform-tools)
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
| 1 | kernel (`boot`) | bucle a ~1 s, `uncompress error` (sin Linux, sin ADB) | **Sí** | Sí (FEL) |
| 2 | `riscv0` | kernel panic a ~3 s, `Load mpp modules` | **Sí** | Sí |
| 3 | `/usr` (`extend`) o `rootfs` | arranca, errores SQUASHFS, reinicio por watchdog a ~20 s (ADB unos 15 s) | No | **Sí (ADB, en la ventana)** |

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

Dos capas, porque **U-Boot no puede escribir la flash** en este firmware (ver §7).

### Capa 1 — U-Boot (env, `autofix_ver=3`)

En cada arranque verifica el CRC32 del kernel. Si está mal y la copia está bien, **arranca con la copia cargada en RAM**:

```
boot_normal  = sunxi_flash read 82000000 ${boot_partition};run af_k;bootm 82000000
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
- La copia solo se usa con la ranura OTA `extendA` (`af_s_${extend_partition}` no existe para `extendB`).
- **Si algo no cuadra, nunca escribe nada y arranca igual que el original** (falla segura, comprobada en hardware).

### Capa 2 — Linux (`/etc/init.d/S00autofix`)

Está en `herramienta/linux/S00autofix` y se instala en el overlay persistente (`rootfs_data`):

- Corre **antes que `S00mpp`**: revisa `riscv0` contra su copia y lo repara **antes de que se cargue** (si no, kernel panic).
- En segundo plano (15 s después) compara el kernel con el MD5 de referencia y reescribe la flash desde la copia. Si la dañada es la copia, la rehace desde el original.
- Guardas: solo actúa si `extend_partition=extendA`, si no hay `swu_mode` (OTA) y si la copia empieza por `ANDROID!` (es nuestra y no una OTA).
- Registro en `/mnt/UDISK/autofix.log` y en la consola (`[autofix] ...`).

### Copia en `extend_backup`

| Offset en extend_backup | Tamaño | Contenido |
|---|---|---|
| 0x000000 | 0x2B0000 | partición `boot` completa (boot.fex + relleno 0xFF) |
| 0x2B0000 | 0x090000 | partición `riscv0` de esa misma cámara (incluida su cola) |

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
| Botón de la placa | Lo lee **U-Boot** (no el BROM) en `sunxi_ext_gpio_ctrl`: imprime `FEL key detecting ...!` cada ~50 ms durante ~3 s. **Mantenido apretado** → `Start up with extflag=2` (modo test de fábrica, `lindipcam -t`): visto 3 veces el 2026-11-23, nunca contó pulsaciones. **Varias pulsaciones** → según el usuario entra en el modo USB con el que flashea PhoenixSuit; el binario tiene `FEL key get %d times !` y `FEL mode confirmed !`, así que cuenta pulsaciones. Funciona en las cámaras muertas por esta falla porque U-Boot está sano (solo se daña el kernel). Pendiente: verlo por UART (cuántas pulsaciones hacen falta) y confirmar que xfel funciona en ese modo |

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
| `adb exec-out` | adbd demasiado viejo: usar `adb shell dd ... of=/tmp/x` + `adb pull` |
| Rutas `/tmp/...` en Git Bash con adb | MSYS las convierte a rutas de Windows: usar `MSYS_NO_PATHCONV=1` |
| Login por UART en Linux | Pide usuario y contraseña (desconocidos): no hace falta, hay ADB root |
| Mantener apretado el botón para FEL | Da `extflag=2` (modo test), no FEL. Hay que **pulsarlo varias veces** al encender |

---

## 10. Rescate de una cámara

| Situación | Cómo |
|---|---|
| Arranca (aunque sea con fallos) | ADB: el .exe en modo ADB, o `adb shell` (root) |
| Bucle por el kernel, **con la protección instalada** | No hace falta nada: arranca con la copia y Linux repara |
| Bucle por el kernel **sin protección** | FEL (pulsar el botón **varias veces** al encender) + .exe (o xfel a mano, §4); luego una pasada por ADB para instalar el script. Plan B: PhoenixSuit + .exe por ADB |
| Bucle en Linux (panic) con `bootdelay=2` y UART | Consola U-Boot (Enter al ver `Hit any key`) → `setenv init /bin/sh` → `run bootcmd` → shell root sin rcS → `dd` desde la copia (`/dev/mtdblock7`) → cortar energía. Montar `/usr` para `md5sum`: `mount -t squashfs -o ro /dev/mtdblock6 /usr` |
| U-Boot con `bootdelay=0` | No se puede interrumpir: FEL |

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

- [ ] **Modo FEL del .exe sin probar en hardware.** Cómo entrar: **pulsar el botón varias veces** durante los ~3 s de `FEL key detecting` (mantenerlo apretado da `extflag=2`). Así entra en el modo en que flashea PhoenixSuit. Falta:
  (1) verlo por UART (debería salir `FEL key get N times !` → `FEL mode confirmed !`) y anotar cuántas pulsaciones hacen falta;
  (2) confirmar que **xfel** responde en ese modo (en la placa 2 sí funcionó xfel, así que probablemente es el mismo);
  (3) correr el .exe en modo FEL sobre una cámara muerta de verdad. Plan B mientras tanto: PhoenixSuit + .exe por ADB.
- [ ] **Bits en el DTB** (final de `boot`, 0x294000–0x2A3000, ~2 % del kernel): U-Boot carga el DTB desde la partición dañada antes del script → Linux se cuelga. Se repara con el .exe (FEL). Idea: usar `set_working_fdt` / `fdt` con el DTB de la copia.
- [ ] **rootfs / usr** no se auto-reparan (no caben como copia). El .exe sí los detecta y los repara. **Ya pasó en una cámara real** (modo 3, §3).
  Idea: **paridad XOR por grupos de bloques** en el espacio libre (quedan 0xD0000 en `extend_backup` tras la copia y
  0x190000 al final de `extend`) + tabla de MD5 por bloque de 64 KB: el bloque dañado se identifica por su MD5 y se
  reconstruye con la paridad XOR del resto del grupo. BusyBox no puede hacer XOR binario en shell → necesita un pequeño
  binario compilado para la cámara (RISC-V rv32, glibc del firmware) o un helper en el kernel. Alternativa más simple:
  corrección de 1 bit por bloque con CRC por bloque (también necesita binario).
- [ ] **Mejora barata:** que la capa U-Boot también se desactive con OTA pendiente (`run af_s_${extend_partition}${swu_mode} ...`). Hoy solo mira `extend_partition`. Si alguna vez se usa OTA en estas cámaras, revisar antes.
- [ ] Si la app o un "reset de fábrica" borra el overlay (`rootfs_data`), se pierde `S00autofix`: la capa U-Boot sigue funcionando, pero ya no se repara la flash ni se protege riscv0. El diagnóstico del .exe lo muestra ("script Linux no instalado").
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
