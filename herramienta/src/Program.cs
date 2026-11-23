// Reparador / Protector de camaras V821 (firmware DY_TX_BM188B v0.1.5)
// - Diagnostica la flash SPI por USB (FEL, via xfel) contra el firmware original.
// - Repara bloques con bits perdidos sin tocar 'private' (emparejamiento).
// - Instala la auto-reparacion: copia de kernel+riscv0 en extend_backup y
//   un script de arranque en el env de U-Boot que verifica CRC y se repara solo.
// Compilar con build.ps1 (csc de .NET Framework 4.x, C# 5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Management;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace ReparadorV821
{
    static class Program
    {
        // Argumentos ocultos para pruebas: --xfel <exe>  --auto diag|repair|simk|simr  --test  --out <log>
        public static string[] Args = new string[0];
        public static string Arg(string name)
        {
            for (int i = 0; i < Args.Length - 1; i++) if (Args[i] == name) return Args[i + 1];
            return null;
        }
        public static bool Flag(string name) { return Array.IndexOf(Args, name) >= 0; }

        [STAThread]
        static void Main(string[] args)
        {
            Args = args;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ------------------------------------------------------------------ CRC32 (zlib)
    static class Crc32
    {
        static readonly uint[] T = MakeTable();
        static uint[] MakeTable()
        {
            uint[] t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                t[i] = c;
            }
            return t;
        }
        public static uint Compute(byte[] d, int off, int len)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = off; i < off + len; i++) c = T[(c ^ d[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
        public static uint Swap(uint v)
        {
            return (v >> 24) | ((v >> 8) & 0xFF00u) | ((v << 8) & 0xFF0000u) | (v << 24);
        }
    }

    // ------------------------------------------------------------------ Mapa de flash y firmware de referencia
    static class Fw
    {
        public const uint BLOCK = 0x10000;
        public const uint PRIVATE = 0x50000;
        public const uint ENV = 0x60000, ENV_LEN = 0x10000;
        public const uint BOOT = 0x70000, BOOT_PART = 0x2B0000;
        public const int BOOT_IMG = 0x2A3000;          // boot.fex (kernel lzma + dtb)
        public const uint RISCV = 0x320000, RISCV_PART = 0x90000;
        public const int RISCV_IMG = 0x8FE1C;          // amp_rv0.fex
        public const uint ROOTFS = 0x3B0000, ROOTFS_USED = 0x2E0000;
        public const uint EXTEND = 0x710000, EXTEND_USED = 0x280000;
        public const uint BACKUP = 0xB20000, BACKUP_USED = 0x340000;  // copia: boot(0x2B0000) + riscv0(0x90000)
        public const int MAX_BITS = 64;                // copia: mas bits que esto = no es nuestra copia
        // Una zona se considera OTRO firmware si mas de MAX_BAD_BLOCKS de sus bloques son distintos.
        // Si casi todos coinciden exacto, es la v0.1.5 con algunos bloques danados (bits perdidos o un
        // bloque entero borrado por un corte de luz): se reparan con el original.
        public const int MAX_BAD_BLOCKS = 4;

        // bit real que fallo en las camaras: boot+0xFBD14, 0x0B -> 0x4B
        public const int FAULT_BOOT_OFF = 0xFBD14;
        public const byte FAULT_MASK = 0x40;

        public static byte[] Boot, Riscv, Rootfs, Usr, EnvOrig;

        static readonly string[,] Golden = {
            { "boot.fex", "75ca6eb5" }, { "amp_rv0.fex", "ba77b129" },
            { "rootfs_nor.fex", "dff95cd2" }, { "usr.fex", "bb034641" }, { "env.fex", "4a86a71e" } };

        public static void Load()
        {
            for (int i = 0; i < Golden.GetLength(0); i++)
            {
                byte[] d = Res.Bytes(Golden[i, 0]);
                string c = Crc32.Compute(d, 0, d.Length).ToString("x8");
                if (c != Golden[i, 1]) throw new Exception("Archivo interno corrupto: " + Golden[i, 0]);
                switch (Golden[i, 0])
                {
                    case "boot.fex": Boot = d; break;
                    case "amp_rv0.fex": Riscv = d; break;
                    case "rootfs_nor.fex": Rootfs = d; break;
                    case "usr.fex": Usr = d; break;
                    case "env.fex": EnvOrig = d; break;
                }
            }
        }

        public static byte[] Padded(byte[] src, int len)
        {
            byte[] r = new byte[len];
            for (int i = 0; i < len; i++) r[i] = 0xFF;
            Buffer.BlockCopy(src, 0, r, 0, Math.Min(src.Length, len));
            return r;
        }

        // Script de auto-reparacion para U-Boot. Solo escribe en la flash si la COPIA
        // tiene el CRC correcto; si algo no cuadra, termina arrancando como el original.
        public static List<KeyValuePair<string, string>> AutofixVars(bool testMode)
        {
            uint kc = Crc32.Compute(Boot, 0, BOOT_IMG);
            var v = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (k, val) => v.Add(new KeyValuePair<string, string>(k, val));

            // Probado en la placa (U-Boot 2018.07 sin hush ni setexpr):
            //  - crc32 <addr> <len> <dst> guarda el CRC como palabra nativa en <dst>
            //  - itest.l *<dst> == <crc> compara contra el valor esperado
            //  - "run A B" ejecuta B solo si A salio bien  -> condicional
            //  - "setenv X ...; run cond setX_ok; run X"   -> si/sino
            //  - boot_normal DEBE empezar con "sunxi_flash read 82000000 ${boot_partition}":
            //    U-Boot lo lee al arrancar para cargar el DTB del kernel.
            //  - sunxi_flash write NO funciona en este U-Boot (solo muestra la ayuda): U-Boot solo
            //    arranca con la copia; la flash la repara Linux (/etc/init.d/S00autofix + afx).
            //  - todas las lecturas a 0x82000000 (U-Boot rechaza cargas en otras zonas).
            //  - riscv0 lo carga Linux desde la flash: lo revisa/repara S00autofix/afx, no U-Boot.
            //  - v4: tambien revisa el CRC de rootfs y usr; si estan danados arranca el MODO RESCATE:
            //    root=/dev/mtdblock8 (jffs2 rootfs_data) init=/upper/etc/autofix/afx -> afx como proceso 1
            //    repara con la paridad y reinicia. Freno: af_rc cuenta intentos (saveenv), maximo 3;
            //    S00autofix lo borra al arrancar normal.
            uint rfc = Crc32.Compute(Rootfs, 0, Rootfs.Length);
            uint usc = Crc32.Compute(Usr, 0, Usr.Length);
            add("autofix_ver", "4");
            add("bootdelay", testMode ? "2" : "0");
            add("boot_normal", "sunxi_flash read 82000000 ${boot_partition};run af_r;run af_k;bootm 82000000");

            add("af_r", "setenv af_bad;run af_s_${extend_partition} af_rr af_ru;run af_resc_${af_bad}");
            add("af_rr", "sunxi_flash read 82000000 rootfs " + Rootfs.Length.ToString("X") + ";crc32 82000000 " + Rootfs.Length.ToString("X") + " 81F00000;setenv af_x setenv af_bad 1;run af_rrchk af_xok;run af_x");
            add("af_rrchk", "itest.l *81F00000 == " + rfc.ToString("x8"));
            add("af_ru", "sunxi_flash read 82000000 extend " + Usr.Length.ToString("X") + ";crc32 82000000 " + Usr.Length.ToString("X") + " 81F00000;setenv af_x setenv af_bad 1;run af_ruchk af_xok;run af_x");
            add("af_ruchk", "itest.l *81F00000 == " + usc.ToString("x8"));
            add("af_xok", "setenv af_x itest 1 == 1");
            add("af_resc_", "itest 1 == 1");                 /* rootfs y usr sanos: nada */
            add("af_resc_1", "echo [autofix] ROOTFS/USR DANADO;run af_cnt_${af_rc} af_rgo");
            add("af_cnt_", "setenv af_rc 1;saveenv");
            add("af_cnt_1", "setenv af_rc 2;saveenv");
            add("af_cnt_2", "setenv af_rc 3;saveenv");
            add("af_rgo", "echo [autofix] MODO RESCATE: afx repara con la paridad;setenv nor_root /dev/mtdblock8;setenv init /upper/etc/autofix/afx rootfstype=jffs2 rw;run setargs_nor");

            add("af_k", "sunxi_flash read 82000000 boot 2A3000;crc32 82000000 2A3000 81F00000;setenv af_kx run af_kfix;run af_kchk af_kok;run af_kx");
            add("af_kchk", "itest.l *81F00000 == " + kc.ToString("x8"));
            add("af_kok", "setenv af_kx echo [autofix] kernel OK");
            // La copia solo vale si la ranura OTA activa es extendA (extend_backup = ranura B de las OTA):
            // af_s_${extend_partition} solo existe para extendA; con extendB "run" falla y no se usa.
            add("af_s_extendA", "itest 1 == 1");
            add("af_kfix", "echo [autofix] KERNEL DANADO, verificando copia;sunxi_flash read 82000000 extend_backup 2B0000;crc32 82000000 2A3000 81F00000;setenv af_kw run af_kbad;run af_s_${extend_partition} af_kchk af_kgo;run af_kw");
            add("af_kgo", "setenv af_kw echo [autofix] copia OK: arrancando con la copia (Linux repara la flash)");
            add("af_kbad", "echo [autofix] copia no valida: arranque normal;sunxi_flash read 82000000 boot");
            return v;
        }
    }

    // ------------------------------------------------------------------ Entorno de U-Boot
    static class UEnv
    {
        public static List<KeyValuePair<string, string>> Parse(byte[] env, out bool crcOk)
        {
            var list = new List<KeyValuePair<string, string>>();
            uint stored = BitConverter.ToUInt32(env, 0);
            crcOk = stored == Crc32.Compute(env, 4, env.Length - 4);
            int i = 4;
            while (i < env.Length && env[i] != 0)
            {
                int j = i;
                while (j < env.Length && env[j] != 0) j++;
                string s = Encoding.ASCII.GetString(env, i, j - i);
                int eq = s.IndexOf('=');
                if (eq > 0) list.Add(new KeyValuePair<string, string>(s.Substring(0, eq), s.Substring(eq + 1)));
                i = j + 1;
            }
            return list;
        }

        public static string Get(List<KeyValuePair<string, string>> l, string k)
        {
            foreach (var kv in l) if (kv.Key == k) return kv.Value;
            return null;
        }

        public static void Set(List<KeyValuePair<string, string>> l, string k, string v)
        {
            for (int i = 0; i < l.Count; i++)
                if (l[i].Key == k) { l[i] = new KeyValuePair<string, string>(k, v); return; }
            l.Add(new KeyValuePair<string, string>(k, v));
        }

        public static byte[] Build(List<KeyValuePair<string, string>> l, int size)
        {
            var ms = new MemoryStream();
            foreach (var kv in l)
            {
                byte[] b = Encoding.ASCII.GetBytes(kv.Key + "=" + kv.Value);
                ms.Write(b, 0, b.Length);
                ms.WriteByte(0);
            }
            ms.WriteByte(0);
            if (ms.Length > size - 4) throw new Exception("El env no cabe en 64 KB");
            byte[] r = new byte[size];
            Buffer.BlockCopy(ms.ToArray(), 0, r, 4, (int)ms.Length);
            uint crc = Crc32.Compute(r, 4, size - 4);
            Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, r, 0, 4);
            return r;
        }

        // Env deseado = env actual de la camara (si es valido) + variables de auto-reparacion.
        public static byte[] Desired(byte[] devEnv, bool testMode, StringBuilder note)
        {
            bool ok;
            var l = Parse(devEnv, out ok);
            if (!ok || Get(l, "boot_normal") == null)
            {
                note.AppendLine("  env de la camara invalido: se usa el env original del firmware como base");
                l = Parse(Fw.EnvOrig, out ok);
            }
            if (Get(l, "boot_normal_orig") == null)
                Set(l, "boot_normal_orig", Get(l, "boot_normal"));
            // quitar variables de versiones anteriores del script (v1: k_<crc>..., v2/v3: af_*)
            l.RemoveAll(kv => Regex.IsMatch(kv.Key, "^(k|kb|r|rb)_(extendA_)?[0-9a-f]+$") || kv.Key.StartsWith("af_"));
            foreach (var kv in Fw.AutofixVars(testMode))
            {
                if (kv.Value.Length > 240) throw new Exception("Variable demasiado larga: " + kv.Key);
                Set(l, kv.Key, kv.Value);
            }
            return Build(l, (int)Fw.ENV_LEN);
        }
    }

    // ------------------------------------------------------------------ Recursos embebidos
    static class Res
    {
        public static byte[] Bytes(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new Exception("Falta recurso interno: " + name);
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }
        // Extrae un recurso embebido. Si ya existe se compara byte a byte (no solo el tamano):
        // un binario reemplazado en %LOCALAPPDATA% se sobrescribe antes de ejecutarlo.
        public static void Extract(string name, string path)
        {
            byte[] d = Bytes(name);
            if (File.Exists(path))
            {
                byte[] cur = File.ReadAllBytes(path);
                bool same = cur.Length == d.Length;
                for (int i = 0; same && i < d.Length; i++) same = cur[i] == d[i];
                if (same) return;
            }
            File.WriteAllBytes(path, d);
        }
    }

    // ------------------------------------------------------------------ acceso a la flash (FEL o ADB)
    interface IFlash
    {
        string Mode { get; }
        byte[] Read(uint addr, uint len, string file);
        void Write(uint addr, byte[] data, string file);
    }

    static class Proc
    {
        public static bool Run(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "TIMEOUT";
                    return false;
                }
                p.WaitForExit();
                output = (o.Result + e.Result).Replace("\r", "").Trim();
                return p.ExitCode == 0;
            }
        }
    }

    // ADB: la camara encendida normal expone "Tina ADB" (root, particiones /dev/mtdblockN escribibles)
    class AdbFlash : IFlash
    {
        public string Exe;
        public string Mode { get { return "ADB"; } }

        // particion -> (mtdblock, direccion absoluta, tamano)
        static readonly uint[,] Parts = {
            { 1, 0x050000, 0x010000 }, { 2, 0x060000, 0x010000 }, { 3, 0x070000, 0x2B0000 },
            { 4, 0x320000, 0x090000 }, { 5, 0x3B0000, 0x360000 }, { 6, 0x710000, 0x410000 },
            { 7, 0xB20000, 0x410000 } };

        public bool Run(string args, int timeoutMs, out string o)
        {
            bool ok = Proc.Run(Exe, args, timeoutMs, out o);
            string low = o.ToLowerInvariant();
            return ok && !low.Contains("error:") && !low.Contains("no devices") && !low.Contains("not found");
        }

        public string Shell(string cmd, int timeoutMs)
        {
            string o;
            Proc.Run(Exe, "shell \"" + cmd + "\"", timeoutMs, out o);
            return o;
        }

        // devuelve los numeros de serie de los dispositivos ADB conectados
        public List<string> Devices()
        {
            string o; var l = new List<string>();
            Proc.Run(Exe, "devices", 20000, out o);
            foreach (string line in o.Split('\n'))
            {
                string[] f = line.Trim().Split('\t');
                if (f.Length == 2 && f[1] == "device") l.Add(f[0]);
            }
            return l;
        }

        static void Map(uint addr, uint len, out uint n, out uint skip, out uint count)
        {
            for (int i = 0; i < Parts.GetLength(0); i++)
                if (addr >= Parts[i, 1] && addr + len <= Parts[i, 1] + Parts[i, 2])
                {
                    if ((addr - Parts[i, 1]) % Fw.BLOCK != 0 || len % Fw.BLOCK != 0) break;
                    n = Parts[i, 0]; skip = (addr - Parts[i, 1]) / Fw.BLOCK; count = len / Fw.BLOCK;
                    return;
                }
            throw new Exception(string.Format("Zona 0x{0:X}+0x{1:X} no valida para ADB", addr, len));
        }

        public byte[] Read(uint addr, uint len, string file)
        {
            uint n, skip, count; Map(addr, len, out n, out skip, out count);
            if (File.Exists(file)) File.Delete(file);
            // /dev/mtdN (crudo): lee la flash de verdad. /dev/mtdblockN puede devolver lo que mtdblock
            // tiene en RAM (ultimo bloque escrito de una particion montada), no lo que hay en la flash.
            Shell(string.Format("dd if=/dev/mtd{0} of=/tmp/af_r.bin bs=65536 skip={1} count={2} 2>/dev/null; sync", n, skip, count), 120000);
            string o;
            bool ok = Run("pull /tmp/af_r.bin \"" + file + "\"", 120000, out o);
            Shell("rm -f /tmp/af_r.bin", 20000);
            if (!ok || !File.Exists(file) || new FileInfo(file).Length != len)
                throw new Exception(string.Format("Fallo la lectura por ADB en 0x{0:X}: {1}", addr, o));
            return File.ReadAllBytes(file);
        }

        public void Write(uint addr, byte[] data, string file)
        {
            uint n, skip, count; Map(addr, (uint)data.Length, out n, out skip, out count);
            File.WriteAllBytes(file, data);
            string o;
            if (!Run("push \"" + file + "\" /tmp/af_w.bin", 120000, out o))
                throw new Exception(string.Format("Fallo el envio por ADB para 0x{0:X}: {1}", addr, o));
            // conv=fsync: obliga a bajar el bloque a la flash (con "sync" solo, mtdblock puede dejar en RAM
            // el ultimo bloque de una particion montada; probado 2026-11-23). La verificacion relee en crudo.
            Shell(string.Format("dd if=/tmp/af_w.bin of=/dev/mtdblock{0} bs=65536 seek={1} count={2} conv=fsync 2>/dev/null; sync; rm -f /tmp/af_w.bin; echo 3 > /proc/sys/vm/drop_caches", n, skip, count), 180000);
        }

        // afx (/etc/autofix/afx + manifest): correccion de 1 bit + paridad para toda la flash fija
        public string AfxMd5()
        {
            Match m = Regex.Match(Shell("md5sum /etc/autofix/afx 2>/dev/null", 20000), "^([0-9a-f]{32})");
            return m.Success ? m.Groups[1].Value : null;
        }

        // ejecuta "afx <args>"; devuelve el codigo de salida (o -1) y las lineas que imprimio
        public int Afx(string args, int timeoutMs, out string output)
        {
            output = Shell("/etc/autofix/afx " + args + " 2>&1; echo afx_rc=$?", timeoutMs);
            Match m = Regex.Match(output, "afx_rc=(\\d+)");
            output = Regex.Replace(output, "afx_rc=\\d+\\s*$", "").Trim();
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        public bool InstallAfx(byte[] bin, string file, string wantMd5, out string o)
        {
            File.WriteAllBytes(file, bin);
            if (!Run("push \"" + file + "\" /tmp/af_x", 60000, out o)) return false;
            Shell("mkdir -p /etc/autofix; cp /tmp/af_x /etc/autofix/afx; chmod 755 /etc/autofix/afx; rm -f /tmp/af_x; sync", 30000);
            o = AfxMd5();
            return o == wantMd5;
        }

        // script de Linux (/etc/init.d/S00autofix, en el overlay persistente)
        public string ScriptMd5()
        {
            string o = Shell("md5sum /etc/init.d/S00autofix 2>/dev/null", 20000);
            Match m = Regex.Match(o, "^([0-9a-f]{32})");
            return m.Success ? m.Groups[1].Value : null;
        }

        public bool InstallScript(byte[] script, string file, string wantMd5, out string o)
        {
            File.WriteAllBytes(file, script);
            if (!Run("push \"" + file + "\" /tmp/af_s", 60000, out o)) return false;
            Shell("cp /tmp/af_s /etc/init.d/S00autofix; chmod 755 /etc/init.d/S00autofix; rm -f /tmp/af_s /etc/init.d/S99autofix; sync", 30000);
            o = ScriptMd5();
            return o == wantMd5;
        }
    }

    // ------------------------------------------------------------------ xfel
    class Xfel : IFlash
    {
        public string Exe;
        public Action<string> Log;
        public string Mode { get { return "FEL"; } }

        public bool Run(string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(Exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.WorkingDirectory = Path.GetDirectoryName(Exe);
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync();
                var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    output = "TIMEOUT";
                    return false;
                }
                p.WaitForExit();
                output = (o.Result + e.Result).Trim();
                string low = output.ToLowerInvariant();
                bool bad = low.Contains("no fel device") || low.Contains("error") || low.Contains("fail") || low.Contains("not support");
                return p.ExitCode == 0 && !bad;
            }
        }

        public byte[] Read(uint addr, uint len, string file)
        {
            string o;
            string args = string.Format("spinor read 0x{0:X} 0x{1:X} \"{2}\"", addr, len, file);
            if (File.Exists(file)) File.Delete(file);
            if (!Run(args, 120000 + (int)(len / 5), out o) || !File.Exists(file) || new FileInfo(file).Length != len)
                throw new Exception(string.Format("Fallo la lectura de flash en 0x{0:X}: {1}", addr, o));
            return File.ReadAllBytes(file);
        }

        public void Write(uint addr, byte[] data, string file)
        {
            File.WriteAllBytes(file, data);
            string o;
            string args = string.Format("spinor write 0x{0:X} \"{1}\"", addr, file);
            if (!Run(args, 180000 + data.Length / 2, out o))
                throw new Exception(string.Format("Fallo la escritura de flash en 0x{0:X}: {1}", addr, o));
        }
    }

    // ------------------------------------------------------------------ Comparacion por bloques
    class RegionCheck
    {
        public string Name;
        public uint Addr;
        public byte[] Dev, Want;
        public int Bits;
        public List<int> Blocks = new List<int>();   // offsets de bloques de 64 KB distintos
        public List<string> Details = new List<string>();

        static readonly int[] Pop = MakePop();
        static int[] MakePop()
        {
            int[] p = new int[256];
            for (int i = 0; i < 256; i++) { int c = 0, x = i; while (x != 0) { c += x & 1; x >>= 1; } p[i] = c; }
            return p;
        }

        public static RegionCheck Compare(string name, uint addr, byte[] dev, byte[] want)
        {
            var r = new RegionCheck();
            r.Name = name; r.Addr = addr; r.Dev = dev; r.Want = want;
            for (int b = 0; b < want.Length; b += (int)Fw.BLOCK)
            {
                int bits = 0;
                int end = Math.Min(b + (int)Fw.BLOCK, want.Length);
                for (int i = b; i < end; i++)
                {
                    int x = dev[i] ^ want[i];
                    if (x != 0)
                    {
                        bits += Pop[x];
                        if (r.Details.Count < 20)
                            r.Details.Add(string.Format("    0x{0:X7} ({1}+0x{2:X}): 0x{3:X2} deberia ser 0x{4:X2}",
                                addr + i, name, i, dev[i], want[i]));
                    }
                }
                if (bits > 0) { r.Blocks.Add(b); r.Bits += bits; }
            }
            return r;
        }

        public byte[] Block(int off)
        {
            int len = Math.Min((int)Fw.BLOCK, Want.Length - off);
            byte[] b = new byte[len];
            Buffer.BlockCopy(Want, off, b, 0, len);
            return b;
        }
    }

    // ------------------------------------------------------------------ Ventana principal
    class MainForm : Form
    {
        Label lblDevice, lblStatus;
        Button btnDiag, btnRepair, btnDriver, btnRestore, btnSimK, btnSimR, btnFolder;
        CheckBox chkTest;
        ProgressBar bar;
        TextBox txt;
        System.Threading.Timer devTimer;
        string binDir, outRoot, sessionDir;
        Xfel xfel;
        AdbFlash adbIo;
        IFlash io;                // transporte de la operacion en curso (ADB o FEL)
        byte[] script;            // /etc/init.d/S00autofix
        string scriptMd5;
        byte[] afxBin;            // /etc/autofix/afx (binario RISC-V estatico)
        string afxMd5;
        volatile bool busy;

        static string Md5(byte[] d)
        {
            using (var h = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(h.ComputeHash(d)).Replace("-", "").ToLowerInvariant();
        }
        StreamWriter logFile;

        public MainForm()
        {
            Text = "Reparador camaras V821 - firmware v0.1.5";
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(900, 600);
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterScreen;

            lblDevice = new Label { Left = 12, Top = 10, Width = 970, Height = 26, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Text = "Buscando camara..." };
            lblDevice.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            btnDiag = Btn("1. Diagnosticar\n(solo lectura)", 12, 44, 170);
            btnRepair = Btn("2. Reparar y\nproteger", 190, 44, 170);
            btnRepair.BackColor = Color.FromArgb(0xD9, 0xF2, 0xD9);
            btnDriver = Btn("Instalar driver USB\n(Zadig, 1 vez por PC)", 400, 44, 180);
            btnRestore = Btn("Restaurar driver\nde PhoenixSuit", 588, 44, 160);
            btnFolder = Btn("Abrir carpeta\nde registros", 756, 44, 130);

            chkTest = new CheckBox { Left = 12, Top = 100, Width = 560, Text = "Modo prueba: U-Boot espera 2 s (consola UART) y se habilita simular fallas" };
            btnSimK = Btn("Simular falla kernel", 580, 96, 160); btnSimK.Height = 30; btnSimK.Visible = false;
            btnSimR = Btn("Simular falla riscv0", 748, 96, 160); btnSimR.Height = 30; btnSimR.Visible = false;
            chkTest.CheckedChanged += (s, e) => { btnSimK.Visible = btnSimR.Visible = chkTest.Checked; };

            bar = new ProgressBar { Left = 12, Top = 134, Width = 970, Height = 16, Minimum = 0, Maximum = 100 };
            bar.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            lblStatus = new Label { Left = 12, Top = 154, Width = 970, Height = 20, Text = "Listo" };
            lblStatus.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

            txt = new TextBox { Left = 12, Top = 178, Width = 976, Height = 510, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9.5f), BackColor = Color.White };
            txt.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;

            Controls.AddRange(new Control[] { lblDevice, btnDiag, btnRepair, btnDriver, btnRestore, btnFolder, chkTest, btnSimK, btnSimR, bar, lblStatus, txt });

            btnDiag.Click += (s, e) => StartJob(false, 0);
            btnRepair.Click += (s, e) => StartJob(true, 0);
            btnSimK.Click += (s, e) => StartJob(false, 1);
            btnSimR.Click += (s, e) => StartJob(false, 2);
            btnDriver.Click += (s, e) => InstallDriver();
            btnRestore.Click += (s, e) => RestoreDriver();
            btnFolder.Click += (s, e) => { Directory.CreateDirectory(outRoot); Process.Start("explorer.exe", "\"" + outRoot + "\""); };

            Load += (s, e) => Init();
            FormClosed += (s, e) =>
            {
                if (devTimer != null) devTimer.Dispose();
                try { string o; if (adbIo != null) Proc.Run(adbIo.Exe, "kill-server", 5000, out o); } catch { }
            };
        }

        Button Btn(string t, int x, int y, int w)
        {
            return new Button { Text = t, Left = x, Top = y, Width = w, Height = 48 };
        }

        void Init()
        {
            try
            {
                binDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReparadorV821");
                Directory.CreateDirectory(binDir);
                Res.Extract("xfel.exe", Path.Combine(binDir, "xfel.exe"));
                Res.Extract("libusb-1.0.dll", Path.Combine(binDir, "libusb-1.0.dll"));
                try
                {
                    Res.Extract("adb.exe", Path.Combine(binDir, "adb.exe"));
                    Res.Extract("AdbWinApi.dll", Path.Combine(binDir, "AdbWinApi.dll"));
                    Res.Extract("AdbWinUsbApi.dll", Path.Combine(binDir, "AdbWinUsbApi.dll"));
                }
                catch (IOException)
                {
                    // Solo llega aqui si el archivo es DISTINTO al embebido y esta bloqueado: no usarlo.
                    throw new Exception("adb.exe en " + binDir + " es distinto al del programa y esta en uso. " +
                        "Cerrar otros programas que usen adb (o reiniciar el PC) y volver a abrir.");
                }
                adbIo = new AdbFlash { Exe = Program.Arg("--adb") ?? Path.Combine(binDir, "adb.exe") };
                script = Res.Bytes("S00autofix");
                scriptMd5 = Md5(script);
                afxBin = Res.Bytes("afx");
                afxMd5 = Md5(afxBin);
                outRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReparadorCamarasV821");
                Fw.Load();
                xfel = new Xfel { Exe = Program.Arg("--xfel") ?? Path.Combine(binDir, "xfel.exe"), Log = Log };
                if (Program.Arg("--outroot") != null) outRoot = Program.Arg("--outroot");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Error al iniciar: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            Log("COMO USAR");
            Log("  CAMARA QUE ARRANCA (lo normal, para instalar la proteccion):");
            Log("    1. Conectar la camara al PC por USB y dejar que encienda normal (sin botones).");
            Log("    2. Esperar ~20 s hasta que arriba diga 'Camara encendida conectada por ADB - lista'.");
            Log("    3. 'Reparar y proteger'. Al terminar en verde, desconectar.");
            Log("  CAMARA MUERTA (no arranca): modo FEL por USB (driver WinUSB con Zadig, 1 vez por PC).");
            Log("    'Reparar y proteger' corrige el bit perdido. Luego, ya arrancando, conectarla normal");
            Log("    y repetir 'Reparar y proteger' para instalar la parte Linux de la proteccion.");
            Log("  Nunca se toca 'private' (emparejamiento con el monitor). Se respalda en el PC.");
            Log("  Registros y respaldos en: " + outRoot);
            Log("");
            devTimer = new System.Threading.Timer(_ => PollDevice(), null, 300, 2000);

            string auto = Program.Arg("--auto");
            if (auto != null)
            {
                chkTest.Checked = Program.Flag("--test");
                autoMode = true;
                int sim = auto == "simk" ? 1 : auto == "simr" ? 2 : 0;
                StartJob(auto == "repair", sim);
            }
        }
        bool autoMode;

        // ---------------------------------------------------------- deteccion USB
        void PollDevice()
        {
            if (busy) return;
            string txtDev; Color col;
            try
            {
                string svc = null; bool found = false, adb = false;
                using (var s = new ManagementObjectSearcher("SELECT DeviceID, Service FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_1F3A&PID_EFE8%'"))
                    foreach (ManagementObject mo in s.Get())
                    {
                        if (mo["DeviceID"].ToString().StartsWith("ROOT", StringComparison.OrdinalIgnoreCase)) continue;
                        found = true;
                        object v = mo["Service"];
                        svc = v == null ? "(sin driver)" : v.ToString();
                    }
                using (var s = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_1F3A&PID_0000%'"))
                    foreach (ManagementObject mo in s.Get()) adb = true;
                if (adb) { txtDev = "Camara encendida conectada por ADB - lista"; col = Color.Green; }
                else if (!found) { txtDev = "Camara no conectada (o todavia arrancando: esperar ~20 s)"; col = Color.Gray; }
                else if (svc != null && svc.Equals("WinUSB", StringComparison.OrdinalIgnoreCase)) { txtDev = "Camara en modo FEL detectada (driver WinUSB) - lista"; col = Color.Green; }
                else { txtDev = "Camara detectada pero con driver '" + svc + "': usar 'Instalar driver USB'"; col = Color.DarkOrange; }
            }
            catch (Exception ex) { txtDev = "No se pudo consultar USB: " + ex.Message; col = Color.Gray; }
            try { BeginInvoke((Action)(() => { lblDevice.Text = txtDev; lblDevice.ForeColor = col; })); } catch { }
        }

        // ---------------------------------------------------------- log / estado
        readonly object logLock = new object();
        void Log(string s)
        {
            lock (logLock) { if (logFile != null) { logFile.WriteLine(s); logFile.Flush(); } }
            UiLog(s);
        }
        void UiLog(string s)
        {
            if (InvokeRequired) { BeginInvoke((Action<string>)UiLog, s); return; }
            txt.AppendText(s + "\r\n");
        }
        void Progress(int pct)
        {
            if (InvokeRequired) { BeginInvoke((Action<int>)Progress, pct); return; }
            bar.Value = Math.Max(0, Math.Min(100, pct));
        }
        void Status(string s, int pct)
        {
            if (InvokeRequired) { BeginInvoke((Action<string, int>)Status, s, pct); return; }
            lblStatus.Text = s;
            if (pct >= 0) bar.Value = Math.Max(0, Math.Min(100, pct));
        }
        void SetBusy(bool b)
        {
            if (InvokeRequired) { BeginInvoke((Action<bool>)SetBusy, b); return; }
            busy = b;
            foreach (Control c in new Control[] { btnDiag, btnRepair, btnDriver, btnRestore, btnSimK, btnSimR, chkTest }) c.Enabled = !b;
            UseWaitCursor = b;
        }
        void Banner(string s, bool ok)
        {
            if (InvokeRequired) { BeginInvoke((Action<string, bool>)Banner, s, ok); return; }
            lblStatus.Text = s;
            lblStatus.ForeColor = ok ? Color.Green : Color.Red;
        }

        // ---------------------------------------------------------- trabajo principal
        void StartJob(bool write, int simulate)
        {
            if (busy) return;
            bool test = chkTest.Checked;
            if (write && test && !autoMode &&
                MessageBox.Show(this, "Modo prueba activo: el env quedara con bootdelay=2 (U-Boot espera 2 s en cada arranque).\n" +
                "Para camaras de clientes desmarcar el modo prueba.\n\nContinuar?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            if (simulate != 0 && !autoMode &&
                MessageBox.Show(this, "Esto DANA a proposito 1 bit de la flash (" + (simulate == 1 ? "kernel, el mismo bit de la falla real" : "riscv0") +
                ") para probar la auto-reparacion.\nUsar solo en la placa de prueba. Continuar?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            lblStatus.ForeColor = SystemColors.ControlText;
            SetBusy(true);
            var th = new Thread(() =>
            {
                try { Job(write, simulate, test); }
                catch (Exception ex) { Log(""); Log("ERROR: " + ex.Message); Banner("ERROR: " + ex.Message, false); }
                finally
                {
                    lock (logLock) { if (logFile != null) { logFile.Dispose(); logFile = null; } }
                    Progress(100);
                    SetBusy(false);
                    if (autoMode)
                        BeginInvoke((Action)(() =>
                        {
                            string outp = Program.Arg("--out");
                            if (outp != null) File.WriteAllText(outp, txt.Text + "\r\nESTADO: " + lblStatus.Text + "\r\n");
                            Close();
                        }));
                }
            });
            th.IsBackground = true;
            th.Start();
        }

        string F(string name) { return Path.Combine(sessionDir, name); }

        void Job(bool write, int simulate, bool test)
        {
            var sw = Stopwatch.StartNew();
            string o;
            Log("==================================================================");
            Log(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (simulate != 0 ? "SIMULAR FALLA" : write ? "REPARAR Y PROTEGER" : "DIAGNOSTICO") + (test ? "  [modo prueba]" : ""));
            Status("Conectando con la camara...", 2);

            // ADB (camara encendida) tiene prioridad; si no hay, FEL (camara muerta)
            string id, info;
            List<string> adbDevs = adbIo.Devices();
            if (adbDevs.Count > 1) throw new Exception("Hay " + adbDevs.Count + " camaras por ADB: conectar solo una.");
            if (adbDevs.Count == 1)
            {
                io = adbIo;
                string cmd = adbIo.Shell("cat /proc/cmdline", 20000);
                if (!cmd.Contains("partitions=private@mtdblock1:env@mtdblock2:boot@mtdblock3:riscv0@mtdblock4"))
                    throw new Exception("El dispositivo ADB no parece esta camara (tabla de particiones distinta).");
                Match m = Regex.Match(cmd, "androidboot.serialno=([0-9a-zA-Z]+)");
                id = m.Success ? m.Groups[1].Value : adbDevs[0];
                info = "Modo ADB (camara encendida). Serie: " + id;
            }
            else
            {
                io = xfel;
                if (!xfel.Run("version", 15000, out o))
                    throw new Exception("No se encuentra la camara: ni encendida por ADB ni en modo FEL. Revisar cable; si esta arrancando, esperar ~20 s. Detalle: " + o);
                string chip = o, sidOut;
                xfel.Run("sid", 15000, out sidOut);
                id = Regex.Replace(sidOut, "[^0-9a-fA-F]", "");
                if (id.Length == 0) id = "sinSID";
                info = "Modo FEL. Chip: " + chip + "  SID: " + sidOut;
            }
            Log(info);

            sessionDir = Path.Combine(outRoot, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + id.Substring(0, Math.Min(16, id.Length)));
            Directory.CreateDirectory(sessionDir);
            lock (logLock)
            {
                logFile = new StreamWriter(F("registro.txt"), true, Encoding.UTF8);
                logFile.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + (simulate != 0 ? "SIMULAR FALLA" : write ? "REPARAR Y PROTEGER" : "DIAGNOSTICO") + (test ? "  [modo prueba]" : ""));
                logFile.WriteLine(info);
            }

            if (io == xfel)
            {
                if (!xfel.Run("ddr", 30000, out o)) Log("Aviso: 'xfel ddr' respondio: " + o);
                if (!xfel.Run("spinor", 20000, out o)) throw new Exception("No se detecta la flash SPI: " + o);
                Log("Flash: " + o);
            }

            if (simulate != 0) { Simulate(simulate); return; }

            // ---- afx (solo ADB): si ya esta instalado, primero que repare lo suyo (private y env con sus
            // copias, 1 bit / paridad). Asi lo que se lee y compara abajo ya parte de lo reparado.
            string afxNow = null, afxCheckOut = ""; int afxRc = -1;
            if (io == adbIo)
            {
                afxNow = adbIo.AfxMd5();
                if (afxNow != null)
                {
                    string ao;
                    if (write)
                    {
                        Status("afx: reparando con copias y paridad...", 3);
                        int r0 = adbIo.Afx("repair", 180000, out ao);
                        if (ao.Length > 0) foreach (string line in ao.Split('\n')) Log("  " + line.Trim());
                        if (r0 == 2) Log("  (afx reparo rootfs/usr: conviene reiniciar la camara al terminar)");
                    }
                    afxRc = adbIo.Afx("check", 180000, out afxCheckOut);
                }
            }

            // ---- lectura
            Status("Leyendo private (respaldo)...", 5);
            byte[] priv = io.Read(Fw.PRIVATE, Fw.BLOCK, F("private_respaldo.bin"));
            Log("private respaldado en la carpeta del registro (" + priv.Length + " bytes)");
            Status("Leyendo env...", 8);
            byte[] env = io.Read(Fw.ENV, Fw.ENV_LEN, F("env_antes.bin"));
            Status("Leyendo kernel (boot)...", 12);
            byte[] boot = io.Read(Fw.BOOT, Fw.BOOT_PART, F("boot_antes.bin"));
            Status("Leyendo riscv0...", 30);
            byte[] rv = io.Read(Fw.RISCV, Fw.RISCV_PART, F("riscv0_antes.bin"));
            Status("Leyendo rootfs...", 35);
            byte[] rootfs = io.Read(Fw.ROOTFS, Fw.ROOTFS_USED, F("rootfs_antes.bin"));
            Status("Leyendo extend (usr)...", 55);
            byte[] ext = io.Read(Fw.EXTEND, Fw.EXTEND_USED, F("extend_antes.bin"));
            Status("Leyendo copia de respaldo (extend_backup)...", 70);
            byte[] bak = io.Read(Fw.BACKUP, Fw.BACKUP_USED, F("extend_backup_antes.bin"));
            string scriptNow = io == adbIo ? adbIo.ScriptMd5() : null;
            Log(string.Format("Lectura completa en {0:0} s", sw.Elapsed.TotalSeconds));

            // ---- analisis
            Status("Analizando...", 85);
            byte[] rvWant = (byte[])rv.Clone();
            Buffer.BlockCopy(Fw.Riscv, 0, rvWant, 0, Fw.RISCV_IMG);   // la cola de la particion se deja como esta
            byte[] bootWant = Fw.Padded(Fw.Boot, (int)Fw.BOOT_PART);
            var regions = new List<RegionCheck>();
            regions.Add(RegionCheck.Compare("boot", Fw.BOOT, boot, bootWant));
            regions.Add(RegionCheck.Compare("riscv0", Fw.RISCV, rv, rvWant));
            regions.Add(RegionCheck.Compare("rootfs", Fw.ROOTFS, rootfs, Fw.Padded(Fw.Rootfs, (int)Fw.ROOTFS_USED)));
            regions.Add(RegionCheck.Compare("extend", Fw.EXTEND, ext, Fw.Padded(Fw.Usr, (int)Fw.EXTEND_USED)));

            byte[] bakWant = new byte[Fw.BACKUP_USED];
            Buffer.BlockCopy(bootWant, 0, bakWant, 0, (int)Fw.BOOT_PART);
            Buffer.BlockCopy(rvWant, 0, bakWant, (int)Fw.BOOT_PART, (int)Fw.RISCV_PART);
            var bakChk = RegionCheck.Compare("extend_backup", Fw.BACKUP, bak, bakWant);
            bool bakEmpty = true;
            foreach (byte x in bak) if (x != 0xFF) { bakEmpty = false; break; }

            var note = new StringBuilder();
            byte[] envWant = UEnv.Desired(env, test, note);
            bool envCrc;
            var envList = UEnv.Parse(env, out envCrc);
            bool protectedNow = envCrc && UEnv.Get(envList, "autofix_ver") != null;
            // Actualizaciones OTA (swupdate A/B): extend_backup es la ranura B. Si la camara
            // esta en extendB o tiene una OTA configurada, no se usa esa zona.
            string slot = envCrc ? UEnv.Get(envList, "extend_partition") : "extendA";
            string swu = envCrc ? UEnv.Get(envList, "swu_mode") : null;
            bool otaUsed = slot != "extendA" || !string.IsNullOrEmpty(swu);

            Log("");
            Log("RESULTADO DEL DIAGNOSTICO");
            bool abort = false;
            foreach (var r in regions)
            {
                if (r.Bits == 0) Log(string.Format("  {0,-14} OK (identico al firmware original)", r.Name));
                else
                {
                    Log(string.Format("  {0,-14} DANADO: {1} bit(s) distintos en {2} bloque(s) de 64 KB", r.Name, r.Bits, r.Blocks.Count));
                    foreach (string d in r.Details) Log(d);
                    if (r.Blocks.Count > Fw.MAX_BAD_BLOCKS) abort = true;
                }
            }
            string bakState;
            if (bakChk.Bits == 0) bakState = "OK (copia de auto-reparacion presente)";
            else if (bakEmpty) bakState = "vacia (sin proteccion instalada)";
            else if (bakChk.Bits <= Fw.MAX_BITS) bakState = string.Format("copia con {0} bit(s) danados (se corrige)", bakChk.Bits);
            else { bakState = "CONTIENE DATOS DESCONOCIDOS (no se toca)"; abort = true; }
            Log(string.Format("  {0,-14} {1}", "extend_backup", bakState));
            bool envSame = BytesEqual(env, envWant);
            Log(string.Format("  {0,-14} {1}", "env", !envCrc ? "CRC invalido (U-Boot usa su env interno)" : envSame ? "OK, auto-reparacion instalada" + (test ? " (modo prueba)" : "") : protectedNow ? "auto-reparacion instalada pero distinta a esta version/modo" : "original, sin auto-reparacion"));
            if (note.Length > 0) Log(note.ToString().TrimEnd());
            bool scriptOk = scriptNow == scriptMd5;
            if (io == adbIo)
                Log(string.Format("  {0,-14} {1}", "script Linux", scriptOk ? "OK (S00autofix instalado)" : scriptNow == null ? "no instalado" : "instalado pero de otra version"));
            else
                Log(string.Format("  {0,-14} {1}", "script Linux", "no se puede revisar por FEL (se instala por ADB con la camara encendida)"));
            bool afxOk = io == adbIo && afxNow == afxMd5 && afxRc == 0;
            if (io == adbIo)
            {
                string st = afxNow == null ? "no instalado (paridad y copias de private/env sin proteger)"
                          : afxNow != afxMd5 ? "instalado pero de otra version"
                          : afxRc == 0 ? "OK (1 bit + paridad: uboot, private, env, boot, riscv0, rootfs, usr)"
                          : afxRc == 1 ? "sin manifiesto (falta 'afx init')"
                          : afxRc == 4 ? "detecto danos reparables" : afxRc == 3 ? "DANOS QUE NO PUEDE REPARAR" : "error (" + afxRc + ")";
                Log(string.Format("  {0,-14} {1}", "afx", st));
                if (afxNow != null && afxRc != 0)
                    foreach (string line in afxCheckOut.Split('\n')) if (line.Trim().Length > 0) Log("      " + line.Trim());
            }
            else
                Log(string.Format("  {0,-14} {1}", "afx", "no se puede revisar por FEL"));
            if (otaUsed)
            {
                Log(string.Format("  OTA: extend_partition={0} swu_mode={1} -> la ranura extend_backup es de las actualizaciones", slot ?? "(no)", swu ?? "(no)"));
                Log("  Esta camara tuvo/tiene una actualizacion OTA: no se instala la proteccion (no se toca extend_backup ni env).");
                abort = true;
            }

            if (abort)
            {
                Log("");
                Log("NO SE ESCRIBE NADA: la flash no coincide con el firmware v0.1.5 (demasiados bloques distintos).");
                Log("Puede ser otra version de firmware u otro modelo. Revisar a mano.");
                Banner("Firmware distinto: no se modifico nada", false);
                return;
            }

            int damaged = 0;
            foreach (var r in regions) damaged += r.Bits;
            if (!write)
            {
                Log("");
                bool allOk = damaged == 0 && bakChk.Bits == 0 && envSame && (io != adbIo || (scriptOk && afxOk));
                Log(allOk ? "Camara sana y protegida." + (io == xfel ? " (Falta revisar el script Linux por ADB.)" : "") : "Para corregir/proteger usar 'Reparar y proteger'.");
                Banner(allOk ? "Camara sana y protegida" : damaged > 0 ? "Camara con " + damaged + " bit(s) danados: usar 'Reparar y proteger'" : "Camara sana, falta instalar la proteccion", allOk);
                return;
            }

            // ---- escritura: 1) bloques danados del firmware, 2) copia de respaldo, 3) env
            Log("");
            Log("ESCRIBIENDO");
            var written = new List<KeyValuePair<uint, byte[]>>();
            int step = 0, total = 0;
            foreach (var r in regions) total += r.Blocks.Count;
            total += bakEmpty ? 1 : bakChk.Blocks.Count;
            total += envSame ? 0 : 1;

            foreach (var r in regions)
                foreach (int off in r.Blocks)
                {
                    uint a = r.Addr + (uint)off;
                    Status(string.Format("Reparando {0} bloque 0x{1:X}...", r.Name, a), 88 + 8 * step++ / Math.Max(1, total));
                    byte[] blk = r.Block(off);
                    io.Write(a, blk, F(string.Format("w_{0:X}.bin", a)));
                    written.Add(new KeyValuePair<uint, byte[]>(a, blk));
                    Log(string.Format("  {0} bloque 0x{1:X} reescrito", r.Name, a));
                }

            if (bakEmpty)
            {
                Status("Escribiendo copia de respaldo (3.2 MB)...", 88 + 8 * step++ / Math.Max(1, total));
                io.Write(Fw.BACKUP, bakWant, F("w_extend_backup.bin"));
                written.Add(new KeyValuePair<uint, byte[]>(Fw.BACKUP, bakWant));
                Log("  copia de kernel + riscv0 escrita en extend_backup");
            }
            else
                foreach (int off in bakChk.Blocks)
                {
                    uint a = Fw.BACKUP + (uint)off;
                    Status(string.Format("Corrigiendo copia, bloque 0x{0:X}...", a), 88 + 8 * step++ / Math.Max(1, total));
                    byte[] blk = bakChk.Block(off);
                    io.Write(a, blk, F(string.Format("w_{0:X}.bin", a)));
                    written.Add(new KeyValuePair<uint, byte[]>(a, blk));
                    Log(string.Format("  extend_backup bloque 0x{0:X} corregido", a));
                }

            if (!envSame)
            {
                Status("Instalando script de auto-reparacion (env)...", 96);
                io.Write(Fw.ENV, envWant, F("env_nuevo.bin"));
                written.Add(new KeyValuePair<uint, byte[]>(Fw.ENV, envWant));
                Log("  env con auto-reparacion escrito" + (test ? " (bootdelay=2, modo prueba)" : ""));
            }

            bool okAll = true;
            if (io == adbIo && !scriptOk)
            {
                Status("Instalando script Linux (S00autofix)...", 96);
                string so;
                if (adbIo.InstallScript(script, F("S00autofix"), scriptMd5, out so)) Log("  script Linux S00autofix instalado y verificado");
                else { okAll = false; Log("  ERROR instalando el script Linux: " + so); }
            }
            else if (written.Count == 0 && afxOk) Log("  nada que escribir");

            // ---- verificacion
            Status("Verificando lo escrito...", 97);
            foreach (var w in written)
            {
                byte[] back = io.Read(w.Key, (uint)w.Value.Length, F(string.Format("v_{0:X}.bin", w.Key)));
                bool eq = BytesEqual(back, w.Value);
                if (!eq) { okAll = false; Log(string.Format("  VERIFICACION FALLO en 0x{0:X}", w.Key)); }
            }

            // ---- afx: instalar/actualizar y (re)generar manifiesto, paridad y copias de private/env.
            // Solo con la flash ya verificada contra el firmware (todo lo anterior OK).
            if (okAll && io == adbIo && (!afxOk || written.Count > 0))
            {
                string ao;
                if (afxNow != afxMd5)
                {
                    Status("Instalando afx...", 98);
                    if (adbIo.InstallAfx(afxBin, F("afx"), afxMd5, out ao)) Log("  afx instalado (/etc/autofix/afx)");
                    else { okAll = false; Log("  ERROR instalando afx: " + ao); }
                }
                if (okAll)
                {
                    Status("afx init: manifiesto, paridad y copias...", 99);
                    int ri = adbIo.Afx("init", 180000, out ao);
                    int rc = adbIo.Afx("check", 180000, out afxCheckOut);
                    if (ri == 0 && rc == 0) Log("  afx init OK: paridad de rootfs+usr y copias de private/env guardadas; check OK");
                    else
                    {
                        okAll = false;
                        Log(string.Format("  ERROR en afx (init={0}, check={1}):", ri, rc));
                        foreach (string line in (ao + "\n" + afxCheckOut).Split('\n')) if (line.Trim().Length > 0) Log("      " + line.Trim());
                    }
                }
            }
            Log(string.Format("Tiempo total: {0:0} s", sw.Elapsed.TotalSeconds));
            if (okAll && io == adbIo)
            {
                Log("");
                Log("LISTO: camara reparada y protegida (U-Boot + script Linux + afx). Se puede desconectar.");
                Banner("LISTO: camara reparada y protegida (" + damaged + " bit(s) corregidos)", true);
            }
            else if (okAll)
            {
                Log("");
                Log("LISTO por FEL: flash reparada + copia + env. Desconectar y dejar arrancar la camara.");
                Log("FALTA la parte Linux: con la camara ya encendida, conectarla normal (sin boton) y");
                Log("volver a pulsar 'Reparar y proteger' (modo ADB) para instalar el script.");
                Banner("Reparada por FEL. Falta conectarla encendida (ADB) para terminar la proteccion", true);
            }
            else
            {
                Log("La verificacion fallo: volver a ejecutar 'Reparar y proteger'.");
                Banner("Verificacion fallo: repetir", false);
            }
        }

        void Simulate(int which)
        {
            uint blockAddr; int offInBlock; byte want;
            if (which == 1)
            {
                int off = Fw.FAULT_BOOT_OFF;
                blockAddr = Fw.BOOT + (uint)(off & ~0xFFFF);
                offInBlock = off & 0xFFFF;
                want = Fw.Boot[off];
            }
            else
            {
                int off = 0x40123;   // byte cualquiera dentro de la imagen riscv0
                blockAddr = Fw.RISCV + (uint)(off & ~0xFFFF);
                offInBlock = off & 0xFFFF;
                want = Fw.Riscv[off];
            }
            Status("Leyendo bloque...", 30);
            byte[] blk = io.Read(blockAddr, Fw.BLOCK, F("sim_antes.bin"));
            if (blk[offInBlock] != want)
                throw new Exception("El bloque no coincide con el firmware original; no se simula.");
            byte[] env = io.Read(Fw.ENV, Fw.ENV_LEN, F("sim_env.bin"));
            bool crc; var l = UEnv.Parse(env, out crc);
            if (!crc || UEnv.Get(l, "autofix_ver") == null)
                Log("AVISO: esta camara NO tiene la auto-reparacion instalada: va a quedar en bucle de reinicio (se arregla con 'Reparar y proteger').");
            if ((want & Fw.FAULT_MASK) != 0) blk[offInBlock] = (byte)(want & ~Fw.FAULT_MASK);
            else blk[offInBlock] = (byte)(want | Fw.FAULT_MASK);
            // riscv0 danado sin el script Linux = kernel panic en bucle (solo se sale por UART o FEL)
            if (io == adbIo && adbIo.ScriptMd5() != scriptMd5)
                throw new Exception("El script Linux no esta instalado: primero 'Reparar y proteger'. No se simula.");
            if (io == xfel && which == 2)
                Log("AVISO: por FEL no se puede comprobar el script Linux; si no esta instalado, la camara quedara en bucle.");
            Status("Escribiendo bit danado...", 60);
            io.Write(blockAddr, blk, F("sim_bloque_danado.bin"));
            byte[] back = io.Read(blockAddr, Fw.BLOCK, F("sim_despues.bin"));
            if (!BytesEqual(back, blk)) throw new Exception("No se pudo escribir el bloque de prueba");
            Log(string.Format("Falla simulada en 0x{0:X}: 0x{1:X2} -> 0x{2:X2}", blockAddr + offInBlock, want, blk[offInBlock]));
            if (io == adbIo)
            {
                adbIo.Shell("sync; reboot", 10000);
                Log("Camara reiniciada. Con UART se ve '[autofix] ...' en U-Boot y en Linux.");
                Log("Esperar ~1 min y pulsar 'Diagnosticar': deberia salir todo OK.");
                Banner("Falla simulada y camara reiniciada: esperar ~1 min y Diagnosticar", true);
            }
            else
            {
                Log("Ahora: desconectar USB, conectar la UART y encender la camara normal.");
                Log("Esperar ~1 min y 'Diagnosticar' (encendida, por ADB): deberia salir todo OK.");
                Banner("Falla simulada. Encender la camara para ver la auto-reparacion", true);
            }
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // ---------------------------------------------------------- drivers
        void InstallDriver()
        {
            string z = Path.Combine(binDir, "zadig.exe");
            try { Res.Extract("zadig.exe", z); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message); return; }
            MessageBox.Show(this,
                "Se abrira Zadig (pide permiso de administrador). Con la camara conectada en modo FEL:\n\n" +
                "1. Menu Options > marcar 'List All Devices'.\n" +
                "2. En la lista elegir el dispositivo con USB ID 1F3A EFE8.\n" +
                "3. A la derecha de la flecha elegir 'WinUSB'.\n" +
                "4. Boton 'Replace Driver' (o 'Install Driver') y esperar.\n\n" +
                "Solo hace falta una vez por PC. PhoenixSuit dejara de ver la camara hasta\n" +
                "usar 'Restaurar driver de PhoenixSuit'.", "Instalar driver USB", MessageBoxButtons.OK, MessageBoxIcon.Information);
            try { Process.Start(z); } catch (Exception ex) { MessageBox.Show(this, "No se pudo abrir Zadig: " + ex.Message); }
        }

        void RestoreDriver()
        {
            if (MessageBox.Show(this, "Quita el driver WinUSB instalado por Zadig para la camara (1F3A:EFE8) para que\n" +
                "PhoenixSuit vuelva a funcionar. Pide permiso de administrador. Continuar?", Text,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            string ps1 = Path.Combine(binDir, "restaurar_driver.ps1");
            string logp = Path.Combine(binDir, "restaurar_driver.log");
            File.WriteAllText(ps1,
                "$ErrorActionPreference='Continue'\r\n" +
                "Start-Transcript -Path '" + logp + "' -Force | Out-Null\r\n" +
                "$infs = Get-ChildItem \"$env:windir\\INF\\oem*.inf\" | Where-Object { $t = Get-Content $_.FullName -Raw; ($t -match 'VID_1F3A&PID_EFE8') -and ($t -match 'libwdi|zadig') }\r\n" +
                "if (-not $infs) { Write-Output 'No hay driver de Zadig para 1F3A:EFE8' }\r\n" +
                "foreach ($i in $infs) { Write-Output ('Quitando ' + $i.Name); pnputil /delete-driver $i.Name /uninstall /force }\r\n" +
                "pnputil /scan-devices\r\n" +
                "Stop-Transcript | Out-Null\r\n", Encoding.UTF8);
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\"");
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                using (var p = Process.Start(psi)) p.WaitForExit();
                Log("Restaurar driver PhoenixSuit:");
                if (File.Exists(logp)) foreach (string line in File.ReadAllLines(logp)) if (!line.StartsWith("*") && line.Trim().Length > 0) Log("  " + line);
                Log("Desconectar y volver a conectar la camara.");
            }
            catch (Exception ex) { Log("No se restauro el driver: " + ex.Message); }
        }
    }
}
