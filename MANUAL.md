# Manual de uso — ReparadorV821.exe

Programa para **reparar** las cámaras del BM Pro 2 que quedan muertas (bucle de reinicio) y para **protegerlas**
y que, si la falla vuelve a pasar, se reparen solas.

Sirve solo para la cámara **V821 con firmware v0.1.5** (`DY_TX_BM188B_v0.1.5`). Si el programa ve otro firmware,
avisa y **no toca nada**.

> Detalles técnicos y el historial de pruebas están en el [README](README.md).

---

## 1. Qué hace

| Botón | Qué hace | ¿Modifica la cámara? |
|---|---|---|
| **1. Diagnosticar (solo lectura)** | Lee la flash y la compara con el firmware original. Dice si hay bits dañados y si la protección está instalada. | No |
| **2. Reparar y proteger** | Corrige los bits dañados, instala la protección y relee todo para verificar. | Sí |
| **Instalar driver USB (Zadig)** | Solo para el modo FEL (cámara muerta). Una vez por PC. | No (cambia el driver del PC) |
| **Restaurar driver de PhoenixSuit** | Deshace lo anterior, para volver a usar PhoenixSuit. | No (cambia el driver del PC) |
| **Abrir carpeta de registros** | Abre la carpeta con los registros y respaldos de cada cámara. | No |

**Nunca toca la partición `private`**, que es donde la cámara guarda el emparejamiento con su monitor. Igual la
respalda en el PC antes de hacer nada.

---

## 2. Requisitos

- Windows 10 u 11. No hay que instalar nada: el .exe trae todo adentro.
- Cable USB de datos (no solo de carga).
- Para cámaras que encienden: nada más. Windows instala solo el driver de ADB la primera vez (se ve como "Tina ADB").
- Para cámaras muertas (modo FEL): el driver WinUSB con Zadig, una vez por PC (ver §5).

---

## 3. Uso normal: proteger una cámara que enciende

Es lo que hay que hacer con **todas** las cámaras, para que no vuelvan a fallar.

1. Abrir `ReparadorV821.exe`.
2. Conectar la cámara al PC por USB y dejar que **encienda normal** (sin apretar ningún botón).
3. Esperar unos 20 s hasta que arriba, en verde, diga **"Camara encendida conectada por ADB - lista"**.
4. Pulsar **2. Reparar y proteger**. Tarda unos 15 s.
5. Abajo debe quedar en verde **"LISTO: camara reparada y protegida"**.
6. Desconectar. Listo.

Para comprobar después, se puede pulsar **1. Diagnosticar**: debe decir **"Camara sana y protegida"**.

> Conectar **una sola cámara** a la vez.

---

## 4a. Cámara que arranca pero se reinicia sola (cada ~20 s)

Es la falla en `/usr`: la cámara enciende, pero a los ~20 s se reinicia, una y otra vez.
El programa la repara por ADB como en el §3, con una diferencia: **hay que pulsar a tiempo**.

1. Conectarla por USB y esperar a que arriba diga **"Camara encendida conectada por ADB - lista"**.
   Ese mensaje aparece y desaparece con cada reinicio.
2. Apenas aparezca, pulsar **2. Reparar y proteger**. Tarda unos 10 s y repara primero los bloques dañados.
3. Si sale un **error** porque se reinició en medio, no pasa nada: esperar a que vuelva a decir "lista" y pulsar
   otra vez. Cuando los bloques dañados quedan reparados, deja de reiniciarse.
4. Pulsar **1. Diagnosticar**: debe decir **"Camara sana y protegida"**.

> Probado en una cámara real el 2026-11-23: se reparó en una sola pasada de 10 s.

---

## 4b. Cámara muerta (no arranca nunca, bucle de reinicio)

Si **nunca** aparece "conectada por ADB", la cámara está muerta de verdad (falla en el kernel).

Hay que entrar en **modo FEL** (modo de grabación del chip por USB).

1. La primera vez en ese PC, instalar el driver (ver §5).
2. **Entrar en modo FEL:** con el **USB conectado al PC**, apenas la cámara enciende (los primeros ~3 s),
   **pulsar el pulsador 5 veces seguidas** (pulsar y soltar). **No dejarlo apretado:** mantenido apretado arranca en
   un modo de prueba de fábrica y no sirve. Arriba debe decir **"Camara en modo FEL detectada (driver WinUSB) - lista"**.
   Si no aparece, desconectar y repetir (si el USB no estaba conectado, la cámara se reinicia y arranca normal).
3. Pulsar **2. Reparar y proteger**. Corrige el bit dañado, deja la copia de respaldo y el arranque protegido.
   Debe terminar en verde con: **"Reparada por FEL. Falta conectarla encendida (ADB) para terminar la proteccion"**.
4. Desconectar y dejar que la cámara **arranque normal**.
5. **Terminar la protección:** con la cámara ya encendida, hacer el §3 (**Reparar y proteger** en modo ADB).
   Por FEL no se puede instalar la parte de Linux de la protección.

> ✅ Probado el 2026-11-23: 5 pulsaciones → modo FEL → el programa reparó 5 bloques en 31 s (uno de ellos
> completamente borrado) y la cámara arrancó sana.
>
> **Plan B** si el modo FEL del programa no funcionara: flashear con PhoenixSuit (mismo método del pulsador) y
> después proteger la cámara por ADB (§3). Comprobar luego que sigue emparejada con su monitor.

---

## 5. Drivers (solo modo FEL)

### Instalar el driver WinUSB (una vez por PC)

1. Cámara en modo FEL conectada.
2. Botón **Instalar driver USB**. Se abre Zadig (pide permiso de administrador).
3. En Zadig: menú **Options → List All Devices**.
4. Elegir el dispositivo con **USB ID 1F3A EFE8**.
5. A la derecha de la flecha elegir **WinUSB** y pulsar **Replace Driver** (o Install Driver).
6. Volver al programa: arriba debe decir "driver WinUSB - lista".

Con esto **PhoenixSuit deja de ver la cámara** en modo FEL.

### Volver a PhoenixSuit

Botón **Restaurar driver de PhoenixSuit** (pide permiso de administrador). Después desconectar y reconectar la cámara.

El modo ADB (cámaras que encienden) no se ve afectado por esto.

---

## 6. Cómo leer el diagnóstico

Ejemplo de una cámara sana y protegida:

```
RESULTADO DEL DIAGNOSTICO
  boot           OK (identico al firmware original)
  riscv0         OK (identico al firmware original)
  rootfs         OK (identico al firmware original)
  extend         OK (identico al firmware original)
  extend_backup  OK (copia de auto-reparacion presente)
  env            OK, auto-reparacion instalada
  script Linux   OK (S00autofix instalado)
  afx            OK (1 bit + paridad: uboot, private, env, boot, riscv0, rootfs, usr)
```

| Línea | Qué significa |
|---|---|
| `boot` | El kernel. Si dice **DANADO: 1 bit(s)**, es la falla típica: **Reparar y proteger** lo corrige. |
| `riscv0` | Programa del coprocesador. Un bit dañado aquí también deja la cámara en bucle. |
| `rootfs`, `extend` | Sistema y programas. Se reparan igual, pero la cámara no se los arregla sola. |
| `extend_backup` | La copia de respaldo. "vacia" = sin protección; "OK" = presente. |
| `env` | El arranque protegido. "original, sin auto-reparacion" = sin protección. |
| `script Linux` | La parte que reescribe la flash sola. "no instalado" = falta (repetir en modo ADB). |
| `afx` | El programa que corrige bits en **toda** la flash (con paridad y copias). "no instalado" o "de otra versión" = pulsar **Reparar y proteger** en modo ADB. Si encontró daños, los lista debajo. |

Bajo cada parte dañada aparece la dirección exacta del byte, por ejemplo:
`0x016BD14 (boot+0xFBD14): 0x4B deberia ser 0x0B` (es la falla real de estas cámaras).

---

## 7. Mensajes y qué hacer

| Mensaje | Qué hacer |
|---|---|
| **LISTO: camara reparada y protegida** | Nada. Desconectar. |
| **Camara sana y protegida** | Nada. |
| **Camara sana, falta instalar la proteccion** | Pulsar **Reparar y proteger**. |
| **Camara con N bit(s) danados** | Pulsar **Reparar y proteger**. |
| **Fallo la lectura/escritura por ADB...** | La cámara se reinició en medio (pasa con la falla de `/usr`, §4a). Esperar a "lista" y repetir. |
| **Reparada por FEL. Falta conectarla encendida (ADB)...** | Dejarla arrancar y repetir **Reparar y proteger** con la cámara encendida. |
| **Firmware distinto: no se modifico nada** | No es la v0.1.5, o tiene demasiadas diferencias. No usar el programa con esta cámara; revisar a mano. |
| **Esta camara tuvo/tiene una actualizacion OTA** | Tiene una actualización del sistema. Por seguridad no se instala la protección. |
| **extend_backup CONTIENE DATOS DESCONOCIDOS** | La zona de la copia está en uso por otra cosa. No se toca. |
| **No se encuentra la camara...** | Revisar el cable. Si está arrancando, esperar 20 s. Si está muerta, ponerla en FEL. |
| **Hay N camaras por ADB** | Desconectar las demás. |
| **Verificacion fallo: repetir** | Volver a pulsar **Reparar y proteger**. Si se repite, revisar cable o puerto USB. |

---

## 8. Registros y respaldos

Cada vez que se usa el programa se crea una carpeta en:

```
Documentos\ReparadorCamarasV821\<fecha>_<número de serie>\
```

| Archivo | Qué es |
|---|---|
| `registro.txt` | Todo lo que mostró el programa |
| `private_respaldo.bin` | Respaldo de `private` (emparejamiento) de esa cámara. **Guardar, no compartir.** |
| `env_antes.bin`, `*_antes.bin` | Cómo estaba la flash antes de tocarla |
| `env_nuevo.bin`, `w_*.bin` | Lo que se escribió |
| `v_*.bin` | La relectura de verificación |

---

## 9. Modo prueba (solo para la placa de pruebas)

La casilla **"Modo prueba"** sirve para validar el sistema con una placa conectada por UART (115200):

- Al proteger, deja `bootdelay=2`: U-Boot espera 2 s en cada arranque y se puede entrar a su consola por la UART.
  **No usar en cámaras de clientes** (el programa avisa).
- Habilita **Simular falla kernel** / **Simular falla riscv0**: daña a propósito 1 bit (el mismo de la falla real)
  y, en modo ADB, reinicia la cámara. Hay que esperar ~1 min y pulsar **Diagnosticar**: debe salir todo OK.
  Por la UART se ve `[autofix] KERNEL DANADO, verificando copia` → `copia OK: arrancando con la copia` →
  `[autofix] kernel reparado OK`.
- Solo deja simular si la protección completa está instalada (si no, la cámara quedaría en bucle).

Para dejar la placa de pruebas como una de cliente: desmarcar "Modo prueba" y **Reparar y proteger** otra vez.

---

## 10. Cómo funciona la protección (resumen)

1. Se guarda una **copia** del kernel y de `riscv0`, una **paridad** de `rootfs` y `/usr`, y copias de `private` y del
   `env`, en una zona libre de la flash (`extend_backup`).
2. En cada arranque, **U-Boot** comprueba el kernel, `rootfs` y `/usr`:
   - si el kernel tiene un bit perdido, **arranca con la copia**;
   - si `rootfs` o `/usr` están dañados, arranca un **modo rescate** en el que el programa **afx** repara la flash con la
     paridad y reinicia la cámara (tarda unos segundos; se ve como un reinicio extra).
3. Al arrancar normal, **afx** revisa toda la flash bloque a bloque: si encuentra un bit perdido, sabe exactamente cuál es
   y lo corrige; si un bloque está muy dañado, lo reconstruye con la paridad o con la copia. Queda anotado en
   `/mnt/UDISK/autofix.log` de la cámara.
4. Además, cada 6 horas reescribe un par de bloques con sus mismos datos ("refresco"), para que las celdas de la flash no
   lleguen a perder carga.

El arranque tarda algo más que de fábrica (~19 s hasta `boot complete` en lugar de ~12 s) por las revisiones.

---

## 11. Preguntas frecuentes

**¿Se pierde el emparejamiento con el monitor?** No: `private` nunca se escribe.

**¿Puedo correrlo dos veces en la misma cámara?** Sí. Si ya está todo bien, no escribe nada.

**¿Y si se corta la luz o el USB a mitad?** Volver a correr **Reparar y proteger**. Lo que se escribe primero
son los bloques dañados y la copia; el arranque protegido se activa al final.

**¿Qué no cubre?** Un bit perdido en el pequeño DTB del final del kernel (~2 % de esa zona) o en el propio U-Boot:
la cámara no se lo arregla sola, pero el programa lo detecta y lo repara (en modo FEL si la cámara no arranca).
`rootfs` y `/usr` **sí** se reparan solos desde la versión con afx.

**¿Por qué tarda más en arrancar?** U-Boot revisa unos 8 MB y afx toda la flash en cada arranque (~7 s más). Es el precio
de que la cámara se arregle sola.

**¿Funciona con otra versión de firmware?** No. Detecta que es distinta y no toca nada.
