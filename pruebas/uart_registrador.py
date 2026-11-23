# Registrador UART. Uso: python uart_registrador.py <carpeta> [COMx]  (necesita pyserial)
# - guarda todo en <carpeta>/uart.log
# - si aparece <carpeta>/tx.txt, envia su contenido (una linea por comando) y lo borra
# - si existe <carpeta>/stop.flag, al ver "Hit any key" en U-Boot pulsa Enter (entra a la consola)
import serial,time,os,sys
d=sys.argv[1]; log=open(os.path.join(d,'uart.log'),'ab',buffering=0)
s=serial.Serial(sys.argv[2] if len(sys.argv)>2 else 'COM15',115200,timeout=0.05)
buf=b''
log.write(b'\n=== inicio registro %s ===\n'%time.strftime('%H:%M:%S').encode())
while True:
    data=s.read(4096)
    if data:
        log.write(data); buf=(buf+data)[-200:]
        if b'Hit any key' in buf and os.path.exists(os.path.join(d,'stop.flag')):
            s.write(b'\r'*5); buf=b''
    tx=os.path.join(d,'tx.txt')
    if os.path.exists(tx):
        try:
            c=open(tx,'rb').read(); os.remove(tx)
            for line in c.replace(b'\r\n',b'\n').split(b'\n'):
                if line or c.endswith(b'\n'):
                    s.write(line+b'\r'); time.sleep(0.3)
        except Exception as e: log.write(('\n[tx error %s]\n'%e).encode())
