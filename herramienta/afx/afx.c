/*
 * afx - auto-reparacion de la flash de las camaras BM Pro 2 (Allwinner V821, firmware v0.1.5)
 *
 * La flash NOR pierde bits (celdas que pierden carga, 0->1). afx:
 *   init     guarda una tabla CRC32+MD5 de cada bloque de 64 KB protegido, la paridad XOR de
 *            rootfs+usr y copias de private y env (llamar SOLO con la flash verificada contra el firmware)
 *   check    revisa todo y dice que esta danado (no escribe)
 *   repair   corrige: 1 bit por CRC (instantaneo), si no, paridad o copia; rehace copias/paridad danadas
 *   refresh N  reescribe N bloques (rotando) con sus mismos datos para "recargar" las celdas
 *
 * Mapa (ver README): mtd0 uboot 5 blq, mtd1 private 1, mtd2 env 1, mtd3 boot 43, mtd4 riscv0 9,
 * mtd5 rootfs (46 usados), mtd6 extend/usr (40 usados), mtd7 extend_backup:
 *   0-42 copia boot, 43-51 copia riscv0, 52 copia private, 53 copia env, 54-64 paridad (11 blq)
 *
 * Lecturas por /dev/mtdN (crudo, sin cache); escrituras por /dev/mtdblockN (bloque de 64 KB entero,
 * atomico respecto de los lectores del sistema de archivos montado).
 * Modo PC para pruebas:  afx -img imagen16MB.bin -m manifiesto.txt <comando>
 *
 * Codigos de salida: 0 todo OK / reparado sin reinicio, 1 error, 2 reparado y hace falta reiniciar
 * (se toco rootfs/usr montados), 3 hay danos que no se pudieron reparar, 4 (check) hay danos reparables.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdarg.h>
#include <time.h>
#ifdef _WIN32
#include <io.h>
#define fsync_fd(fd) _commit(fd)
#else
#include <unistd.h>
#define fsync_fd(fd) fsync(fd)
#endif

#define BLK 65536
#define NPAR 11
#define BAK_PRIV 52
#define BAK_ENV 53
#define BAK_PAR 54
#define AFX_VER "afx 1"

/* ------------------------------------------------------------------ regiones */
enum { K_ONEBIT, K_DYN, K_ENV, K_PAIR, K_PARITY, K_BAK };
typedef struct { const char *name; int mtd; uint32_t base; int nblk; int kind; int bakfirst; } Region;
static Region R[] = {
    { "uboot",   0, 0x000000,  5, K_ONEBIT, -1 },
    { "private", 1, 0x050000,  1, K_DYN,    BAK_PRIV },
    { "env",     2, 0x060000,  1, K_ENV,    BAK_ENV },
    { "boot",    3, 0x070000, 43, K_PAIR,   0 },
    { "riscv0",  4, 0x320000,  9, K_PAIR,   43 },
    { "rootfs",  5, 0x3B0000, 46, K_PARITY, -1 },
    { "usr",     6, 0x710000, 40, K_PARITY, -1 },
    { "bak",     7, 0xB20000, 65, K_BAK,    -1 },
};
#define NREG (int)(sizeof(R) / sizeof(R[0]))
enum { RG_UBOOT, RG_PRIV, RG_ENV, RG_BOOT, RG_RISCV, RG_ROOTFS, RG_USR, RG_BAK };

static const char *g_img = NULL;           /* modo PC: imagen de 16 MB */
static const char *g_man = "/etc/autofix/manifest";
static const char *g_log = "/mnt/UDISK/autofix.log";
static const char *g_state = "/mnt/UDISK/autofix.state";
static int g_verbose = 0, g_dry = 0;

/* tabla del manifiesto: crc + md5 de cada bloque protegido */
typedef struct { uint32_t crc; uint8_t md5[16]; int have; } Ent;
static Ent E[NREG][65];

/* ------------------------------------------------------------------ log */
static void logm(const char *fmt, ...)
{
    char buf[512]; va_list ap; va_start(ap, fmt); vsnprintf(buf, sizeof buf, fmt, ap); va_end(ap);
    printf("[afx] %s\n", buf); fflush(stdout);
    if (!g_img && !g_dry) {
        FILE *f = fopen(g_log, "a");
        if (f) {
            time_t t = time(NULL); struct tm *tm = localtime(&t); char ts[32];
            strftime(ts, sizeof ts, "%Y-%m-%d %H:%M:%S", tm);
            fprintf(f, "%s afx %s\n", ts, buf); fclose(f);
        }
        FILE *c = fopen("/dev/console", "w");
        if (c) { fprintf(c, "[autofix] %s\n", buf); fclose(c); }
    }
}

/* ------------------------------------------------------------------ CRC32 (zlib) */
static uint32_t T[256];
static void crc_init(void)
{
    for (uint32_t i = 0; i < 256; i++) {
        uint32_t c = i;
        for (int k = 0; k < 8; k++) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        T[i] = c;
    }
}
static uint32_t crc32b(const uint8_t *d, size_t n)
{
    uint32_t c = 0xFFFFFFFFu;
    for (size_t i = 0; i < n; i++) c = T[(c ^ d[i]) & 0xFF] ^ (c >> 8);
    return c ^ 0xFFFFFFFFu;
}

/* Ubica un solo bit danado: CRC(dato^e) = CRC(dato) ^ CRC0(e) (CRC0 = parte lineal, sin init/xorout).
 * Para e = bit b del byte j:  CRC0 = Z^(n-1-j)( T[1<<b] ),  con Z(r) = (r>>8) ^ T[r&0xff]
 * (procesar un byte cero). Se recorre j de n-1 a 0 aplicando Z: O(8n), instantaneo.
 * Devuelve el indice de bit (j*8+b) o -1. */
static long locate_bit(uint32_t syndrome, size_t n)
{
    uint32_t v[8];
    for (int b = 0; b < 8; b++) v[b] = T[1u << b];
    for (long j = (long)n - 1; j >= 0; j--) {
        for (int b = 0; b < 8; b++) {
            if (v[b] == syndrome) return j * 8 + b;
            v[b] = (v[b] >> 8) ^ T[v[b] & 0xFF];
        }
    }
    return -1;
}

/* ------------------------------------------------------------------ MD5 (RFC 1321) */
typedef struct { uint32_t s[4]; uint64_t n; uint8_t b[64]; } Md5;
static const uint32_t MK[64] = {
    0xd76aa478,0xe8c7b756,0x242070db,0xc1bdceee,0xf57c0faf,0x4787c62a,0xa8304613,0xfd469501,
    0x698098d8,0x8b44f7af,0xffff5bb1,0x895cd7be,0x6b901122,0xfd987193,0xa679438e,0x49b40821,
    0xf61e2562,0xc040b340,0x265e5a51,0xe9b6c7aa,0xd62f105d,0x02441453,0xd8a1e681,0xe7d3fbc8,
    0x21e1cde6,0xc33707d6,0xf4d50d87,0x455a14ed,0xa9e3e905,0xfcefa3f8,0x676f02d9,0x8d2a4c8a,
    0xfffa3942,0x8771f681,0x6d9d6122,0xfde5380c,0xa4beea44,0x4bdecfa9,0xf6bb4b60,0xbebfbc70,
    0x289b7ec6,0xeaa127fa,0xd4ef3085,0x04881d05,0xd9d4d039,0xe6db99e5,0x1fa27cf8,0xc4ac5665,
    0xf4292244,0x432aff97,0xab9423a7,0xfc93a039,0x655b59c3,0x8f0ccc92,0xffeff47d,0x85845dd1,
    0x6fa87e4f,0xfe2ce6e0,0xa3014314,0x4e0811a1,0xf7537e82,0xbd3af235,0x2ad7d2bb,0xeb86d391 };
static const uint8_t MR[64] = { 7,12,17,22,7,12,17,22,7,12,17,22,7,12,17,22,5,9,14,20,5,9,14,20,5,9,14,20,5,9,14,20,
    4,11,16,23,4,11,16,23,4,11,16,23,4,11,16,23,6,10,15,21,6,10,15,21,6,10,15,21,6,10,15,21 };
static void md5_blk(Md5 *m, const uint8_t *p)
{
    uint32_t w[16], a = m->s[0], b = m->s[1], c = m->s[2], d = m->s[3];
    for (int i = 0; i < 16; i++) w[i] = p[i*4] | (p[i*4+1] << 8) | (p[i*4+2] << 16) | ((uint32_t)p[i*4+3] << 24);
    for (int i = 0; i < 64; i++) {
        uint32_t f; int g;
        if (i < 16) { f = (b & c) | (~b & d); g = i; }
        else if (i < 32) { f = (d & b) | (~d & c); g = (5*i + 1) & 15; }
        else if (i < 48) { f = b ^ c ^ d; g = (3*i + 5) & 15; }
        else { f = c ^ (b | ~d); g = (7*i) & 15; }
        uint32_t t = d; d = c; c = b;
        uint32_t x = a + f + MK[i] + w[g];
        b = b + ((x << MR[i]) | (x >> (32 - MR[i]))); a = t;
    }
    m->s[0] += a; m->s[1] += b; m->s[2] += c; m->s[3] += d;
}
static void md5(const uint8_t *d, size_t n, uint8_t out[16])
{
    Md5 m = { { 0x67452301, 0xefcdab89, 0x98badcfe, 0x10325476 }, 0, { 0 } };
    size_t i = 0;
    for (; i + 64 <= n; i += 64) md5_blk(&m, d + i);
    uint8_t t[128] = { 0 }; size_t r = n - i; memcpy(t, d + i, r); t[r] = 0x80;
    size_t L = (r < 56) ? 64 : 128; uint64_t bits = (uint64_t)n * 8;
    for (int k = 0; k < 8; k++) t[L - 8 + k] = (uint8_t)(bits >> (8 * k));
    md5_blk(&m, t); if (L == 128) md5_blk(&m, t + 64);
    for (int k = 0; k < 4; k++) for (int j = 0; j < 4; j++) out[k*4 + j] = (uint8_t)(m.s[k] >> (8 * j));
}
static void hex(const uint8_t *d, int n, char *o) { for (int i = 0; i < n; i++) sprintf(o + 2*i, "%02x", d[i]); }

/* ------------------------------------------------------------------ acceso a la flash */
static int rd(int rg, int blk, uint8_t *buf)
{
    char p[64]; FILE *f; long off;
    if (g_img) { f = fopen(g_img, "rb"); off = (long)(R[rg].base + (uint32_t)blk * BLK); }
    else {
        snprintf(p, sizeof p, "/dev/mtd%d", R[rg].mtd); f = fopen(p, "rb");
        if (!f) { snprintf(p, sizeof p, "/dev/mtdblock%d", R[rg].mtd); f = fopen(p, "rb"); }
        off = (long)blk * BLK;
    }
    if (!f) return -1;
    int ok = fseek(f, off, SEEK_SET) == 0 && fread(buf, 1, BLK, f) == BLK;
    fclose(f);
    return ok ? 0 : -1;
}
static int wr(int rg, int blk, const uint8_t *buf)
{
    if (g_dry) { logm("(simulacion) escribiria %s bloque %d", R[rg].name, blk); return 0; }
    char p[64]; FILE *f; long off;
    if (g_img) { f = fopen(g_img, "r+b"); off = (long)(R[rg].base + (uint32_t)blk * BLK); }
    else { snprintf(p, sizeof p, "/dev/mtdblock%d", R[rg].mtd); f = fopen(p, "r+b"); off = (long)blk * BLK; }
    if (!f) return -1;
    setvbuf(f, NULL, _IONBF, 0);
    int ok = fseek(f, off, SEEK_SET) == 0 && fwrite(buf, 1, BLK, f) == BLK && fflush(f) == 0 && fsync_fd(fileno(f)) == 0;
    fclose(f);
    if (!ok) return -1;
    uint8_t *chk = malloc(BLK);          /* releer de la flash cruda y comparar */
    ok = chk && rd(rg, blk, chk) == 0 && memcmp(chk, buf, BLK) == 0;
    free(chk);
    return ok ? 0 : -1;
}

/* ------------------------------------------------------------------ manifiesto */
static int man_load(void)
{
    FILE *f = fopen(g_man, "r");
    if (!f) return -1;
    char line[256]; int n = 0, ok = 0;
    while (fgets(line, sizeof line, f)) {
        if (!strncmp(line, AFX_VER, strlen(AFX_VER))) { ok = 1; continue; }
        char name[16], m[40]; int b; unsigned c;
        if (sscanf(line, "B %15s %d %x %32s", name, &b, &c, m) == 4) {
            for (int r = 0; r < NREG; r++) if (!strcmp(R[r].name, name) && b >= 0 && b < 65) {
                E[r][b].crc = c; E[r][b].have = 1;
                for (int i = 0; i < 16; i++) { unsigned x; sscanf(m + 2*i, "%2x", &x); E[r][b].md5[i] = (uint8_t)x; }
                n++;
            }
        }
    }
    fclose(f);
    return ok && n > 0 ? 0 : -1;
}
static int man_save(void)
{
    char tmp[256]; snprintf(tmp, sizeof tmp, "%s.tmp", g_man);
    FILE *f = fopen(tmp, "w");
    if (!f) return -1;
    fprintf(f, "%s firmware v0.1.5\n", AFX_VER);
    for (int r = 0; r < NREG; r++)
        for (int b = 0; b < R[r].nblk; b++) if (E[r][b].have) {
            char h[33]; hex(E[r][b].md5, 16, h);
            fprintf(f, "B %s %d %08x %s\n", R[r].name, b, E[r][b].crc, h);
        }
    fflush(f); fsync_fd(fileno(f)); fclose(f);
    remove(g_man);
    return rename(tmp, g_man);
}
static void ent_set(int rg, int b, const uint8_t *buf) { E[rg][b].crc = crc32b(buf, BLK); md5(buf, BLK, E[rg][b].md5); E[rg][b].have = 1; }
static int ent_ok(int rg, int b, const uint8_t *buf)
{
    if (!E[rg][b].have || crc32b(buf, BLK) != E[rg][b].crc) return 0;
    uint8_t h[16]; md5(buf, BLK, h); return memcmp(h, E[rg][b].md5, 16) == 0;
}
/* intenta corregir 1 bit; deja el bloque corregido en buf. 1 = corregido */
static int fix1(int rg, int b, uint8_t *buf, long *bitpos)
{
    if (!E[rg][b].have) return 0;
    long k = locate_bit(crc32b(buf, BLK) ^ E[rg][b].crc, BLK);
    if (k < 0) return 0;
    buf[k / 8] ^= (uint8_t)(1u << (k % 8));
    if (ent_ok(rg, b, buf)) { if (bitpos) *bitpos = k; return 1; }
    buf[k / 8] ^= (uint8_t)(1u << (k % 8));
    return 0;
}

/* miembros de la paridad: rootfs 0-45 y usr 0-39 -> indice i, grupo i % NPAR */
static int par_count(void) { return R[RG_ROOTFS].nblk + R[RG_USR].nblk; }
static void par_member(int i, int *rg, int *b)
{
    if (i < R[RG_ROOTFS].nblk) { *rg = RG_ROOTFS; *b = i; } else { *rg = RG_USR; *b = i - R[RG_ROOTFS].nblk; }
}

/* ------------------------------------------------------------------ env de U-Boot */
static int env_valid(const uint8_t *e)
{
    uint32_t st = e[0] | (e[1] << 8) | (e[2] << 16) | ((uint32_t)e[3] << 24);
    return st == crc32b(e + 4, BLK - 4);
}
/* diferencia de bits: devuelve cuantos; si es 1, *pos y si es 0->1 en a respecto de b */
static int bitdiff(const uint8_t *a, const uint8_t *b, long *pos, int *a_has_1)
{
    int n = 0;
    for (long i = 0; i < BLK; i++) {
        uint8_t x = a[i] ^ b[i];
        while (x) {
            int k = __builtin_ctz(x); x &= x - 1;
            if (++n == 1) { *pos = i * 8 + k; *a_has_1 = (a[i] >> k) & 1; }
            if (n > 1) return n;
        }
    }
    return n;
}

/* ------------------------------------------------------------------ init */
static int cmd_init(void)
{
    uint8_t *buf = malloc(BLK), *par = calloc(NPAR, BLK);
    if (!buf || !par) return 1;
    memset(E, 0, sizeof E);
    for (int r = 0; r < NREG; r++) {
        if (r == RG_BAK) continue;
        for (int b = 0; b < R[r].nblk; b++) {
            if (rd(r, b, buf)) { logm("ERROR leyendo %s %d", R[r].name, b); return 1; }
            ent_set(r, b, buf);
            if (R[r].kind == K_PAIR) ent_set(RG_BAK, R[r].bakfirst + b, buf);   /* la copia debe ser igual */
            if (r == RG_PRIV || r == RG_ENV) {
                if (wr(RG_BAK, R[r].bakfirst, buf)) { logm("ERROR escribiendo copia de %s", R[r].name); return 1; }
                ent_set(RG_BAK, R[r].bakfirst, buf);
            }
        }
    }
    for (int i = 0; i < par_count(); i++) {
        int rg, b; par_member(i, &rg, &b);
        if (rd(rg, b, buf)) return 1;
        uint8_t *p = par + (size_t)(i % NPAR) * BLK;
        for (int k = 0; k < BLK; k++) p[k] ^= buf[k];
    }
    for (int g = 0; g < NPAR; g++) {
        if (wr(RG_BAK, BAK_PAR + g, par + (size_t)g * BLK)) { logm("ERROR escribiendo paridad %d", g); return 1; }
        ent_set(RG_BAK, BAK_PAR + g, par + (size_t)g * BLK);
    }
    /* verificar que las copias de boot/riscv0 ya presentes coinciden */
    int bad = 0;
    for (int b = 0; b < BAK_PRIV; b++) { if (rd(RG_BAK, b, buf) || !ent_ok(RG_BAK, b, buf)) bad++; }
    if (bad) logm("AVISO: %d bloques de la copia boot/riscv0 no coinciden (se repararan con 'repair')", bad);
    if (man_save()) { logm("ERROR guardando manifiesto %s", g_man); return 1; }
    int tot = 0;
    for (int r = 0; r < NREG; r++) for (int b = 0; b < 65; b++) tot += E[r][b].have;
    logm("init OK: %d bloques protegidos, paridad de %d bloques en %d grupos", tot, par_count(), NPAR);
    free(buf); free(par);
    return 0;
}

/* ------------------------------------------------------------------ check / repair */
static int g_fixed = 0, g_reboot = 0, g_unfixed = 0, g_damaged = 0;

static void report_fix(int rg, int b, long k, const char *how)
{
    if (k >= 0) logm("%s bloque %d: bit perdido en 0x%07lX bit %ld -> %s",
                     R[rg].name, b, (unsigned long)(R[rg].base + (uint32_t)b * BLK + (uint32_t)(k / 8)), k % 8, how);
    else logm("%s bloque %d -> %s", R[rg].name, b, how);
}

/* bloque fijo con 1 bit o fuente alternativa (alt_rg/alt_b >=0). repair=1 escribe */
static int fix_block(int rg, int b, int alt_rg, int alt_b, int repair, uint8_t *buf, uint8_t *tmp)
{
    if (rd(rg, b, buf)) { logm("ERROR leyendo %s %d", R[rg].name, b); g_unfixed++; return -1; }
    if (ent_ok(rg, b, buf)) return 0;
    g_damaged++;
    long k = -1;
    if (fix1(rg, b, buf, &k)) {
        if (!repair) { report_fix(rg, b, k, "se puede corregir (1 bit)"); return 1; }
        if (wr(rg, b, buf) == 0) { report_fix(rg, b, k, "corregido (1 bit)"); g_fixed++; return 1; }
    } else if (alt_rg >= 0 && rd(alt_rg, alt_b, tmp) == 0 && ent_ok(rg, b, tmp)) {
        if (!repair) { report_fix(rg, b, -1, "danado (varios bits), se puede restaurar desde la copia"); return 1; }
        if (wr(rg, b, tmp) == 0) { report_fix(rg, b, -1, "restaurado desde la copia"); g_fixed++; return 1; }
    }
    report_fix(rg, b, -1, repair ? "NO SE PUDO REPARAR aqui" : "danado (varios bits)");
    if (repair) g_unfixed++;
    return -1;
}

static int cmd_check(int repair)
{
    if (man_load()) { logm("ERROR: no hay manifiesto (%s): ejecutar 'afx init'", g_man); return 1; }
    uint8_t *buf = malloc(BLK), *tmp = malloc(BLK), *acc = malloc(BLK);
    if (!buf || !tmp || !acc) return 1;

    /* uboot: solo corregir 1 bit (puede cambiar legitimamente: no se restaura entero) */
    for (int b = 0; b < R[RG_UBOOT].nblk; b++) {
        if (rd(RG_UBOOT, b, buf)) continue;
        if (ent_ok(RG_UBOOT, b, buf)) continue;
        long k;
        if (fix1(RG_UBOOT, b, buf, &k)) {
            g_damaged++;
            if (!repair) report_fix(RG_UBOOT, b, k, "se puede corregir (1 bit)");
            else if (wr(RG_UBOOT, b, buf) == 0) { report_fix(RG_UBOOT, b, k, "corregido (1 bit)"); g_fixed++; }
            else g_unfixed++;
        } else if (repair) { logm("uboot bloque %d cambio (varios bits): se toma como cambio legitimo", b); ent_set(RG_UBOOT, b, buf); man_save(); }
        else logm("uboot bloque %d distinto al manifiesto (varios bits)", b);
    }

    /* boot y riscv0 contra su copia, y la copia contra el original */
    for (int rg = RG_BOOT; rg <= RG_RISCV; rg++)
        for (int b = 0; b < R[rg].nblk; b++) {
            int cb = R[rg].bakfirst + b;
            fix_block(rg, b, RG_BAK, cb, repair, buf, tmp);
            fix_block(RG_BAK, cb, rg, b, repair, buf, tmp);
        }

    /* private (dinamico) y env: copia en extend_backup */
    for (int rg = RG_PRIV; rg <= RG_ENV; rg++) {
        int cb = R[rg].bakfirst;
        if (rd(rg, 0, buf) || rd(RG_BAK, cb, tmp)) { logm("ERROR leyendo %s", R[rg].name); continue; }
        if (!memcmp(buf, tmp, BLK)) continue;
        long pos = 0; int p1 = 0; int nd = bitdiff(buf, tmp, &pos, &p1);
        int prim_bad, copy_bad;
        if (rg == RG_ENV) { prim_bad = !env_valid(buf) && env_valid(tmp); copy_bad = env_valid(buf) && !prim_bad; }
        else { prim_bad = (nd == 1 && p1); copy_bad = !prim_bad; }   /* 1 bit 0->1 en el original = dano */
        g_damaged++;
        if (prim_bad) {
            if (!repair) logm("%s danado (%d bit/s): se puede restaurar desde la copia", R[rg].name, nd);
            else if (wr(rg, 0, tmp) == 0) { logm("%s danado: restaurado desde la copia", R[rg].name); g_fixed++; }
            else g_unfixed++;
        } else if (copy_bad) {
            const char *why = (nd == 1 && !p1) ? "copia con 1 bit perdido" : "cambio legitimo";
            if (!repair) logm("%s distinto de su copia (%s): se actualizara la copia", R[rg].name, why);
            else if (wr(RG_BAK, cb, buf) == 0) {
                logm("%s: %s -> copia actualizada", R[rg].name, why);
                ent_set(RG_BAK, cb, buf); ent_set(rg, 0, buf); man_save(); g_fixed++;
            }
            else g_unfixed++;
        } else { logm("%s y su copia invalidos: no se toca", R[rg].name); g_unfixed++; }
    }

    /* rootfs + usr: 1 bit por CRC; si no, paridad */
    int np = par_count();
    for (int i = 0; i < np; i++) {
        int rg, b; par_member(i, &rg, &b);
        if (rd(rg, b, buf)) { g_unfixed++; continue; }
        if (ent_ok(rg, b, buf)) continue;
        g_damaged++;
        long k = -1;
        int ok = fix1(rg, b, buf, &k);
        const char *how = "corregido (1 bit)";
        if (!ok) {                                   /* reconstruir con la paridad del grupo */
            int g = i % NPAR, good = 1;
            if (rd(RG_BAK, BAK_PAR + g, acc) || !(ent_ok(RG_BAK, BAK_PAR + g, acc) || fix1(RG_BAK, BAK_PAR + g, acc, NULL))) good = 0;
            for (int j = g; good && j < np; j += NPAR) {
                if (j == i) continue;
                int r2, b2; par_member(j, &r2, &b2);
                if (rd(r2, b2, tmp) || !(ent_ok(r2, b2, tmp) || fix1(r2, b2, tmp, NULL))) { good = 0; break; }
                for (int x = 0; x < BLK; x++) acc[x] ^= tmp[x];
            }
            if (good && ent_ok(rg, b, acc)) { memcpy(buf, acc, BLK); ok = 1; how = "reconstruido con la paridad"; k = -1; }
        }
        if (!ok) { report_fix(rg, b, -1, "NO SE PUEDE REPARAR (mas de un bloque danado en su grupo)"); g_unfixed++; continue; }
        if (!repair) { report_fix(rg, b, k, ok ? "se puede reparar" : "?"); continue; }
        if (wr(rg, b, buf) == 0) { report_fix(rg, b, k, how); g_fixed++; g_reboot = 1; }
        else { report_fix(rg, b, -1, "ERROR al escribir"); g_unfixed++; }
    }

    /* bloques de paridad: 1 bit, o recalcular si los datos estan sanos */
    for (int g = 0; g < NPAR; g++) {
        int pb = BAK_PAR + g;
        if (rd(RG_BAK, pb, buf) || ent_ok(RG_BAK, pb, buf)) continue;
        g_damaged++;
        long k = -1;
        int ok = fix1(RG_BAK, pb, buf, &k);
        if (!ok) {
            memset(acc, 0, BLK); ok = 1;
            for (int j = g; j < np; j += NPAR) {
                int r2, b2; par_member(j, &r2, &b2);
                if (rd(r2, b2, tmp) || !ent_ok(r2, b2, tmp)) { ok = 0; break; }
                for (int x = 0; x < BLK; x++) acc[x] ^= tmp[x];
            }
            if (ok && ent_ok(RG_BAK, pb, acc)) memcpy(buf, acc, BLK); else ok = 0;
        }
        if (!ok) { logm("paridad %d danada y no se puede recalcular", g); g_unfixed++; continue; }
        if (!repair) { logm("paridad %d danada: se puede reparar", g); continue; }
        if (wr(RG_BAK, pb, buf) == 0) { logm("paridad %d reparada", g); g_fixed++; } else g_unfixed++;
    }

    free(buf); free(tmp); free(acc);
    if (!g_damaged) { if (g_verbose || !repair) logm("todo OK"); return 0; }
    logm("resumen: %d danados, %d reparados, %d sin reparar%s", g_damaged, g_fixed, g_unfixed,
         g_reboot ? " (hace falta reiniciar)" : "");
    if (g_unfixed) return 3;
    if (!repair) return 4;                 /* check: hay danos (reparables) */
    return g_reboot ? 2 : 0;
}

/* ------------------------------------------------------------------ refresh */
/* recorre todos los bloques protegidos; reescribe n bloques sanos con sus mismos datos */
static int cmd_refresh(int n)
{
    if (man_load()) { logm("ERROR: no hay manifiesto"); return 1; }
    int list_rg[256], list_b[256], tot = 0;
    for (int r = 0; r < NREG; r++) {
        /* uboot nunca se reescribe por rutina; private y env tampoco (la app podria estar escribiendolos):
           se refrescan sus copias y, si pierden un bit, repair los restaura */
        if (r == RG_UBOOT || r == RG_PRIV || r == RG_ENV) continue;
        for (int b = 0; b < R[r].nblk; b++) if (E[r][b].have) { list_rg[tot] = r; list_b[tot] = b; tot++; }
    }
    int idx = 0; FILE *s = fopen(g_state, "r");
    if (s) { if (fscanf(s, "%d", &idx) != 1) idx = 0; fclose(s); }
    if (idx < 0 || idx >= tot) idx = 0;
    uint8_t *buf = malloc(BLK); int done = 0;
    for (int i = 0; i < n && i < tot; i++, idx = (idx + 1) % tot) {
        int r = list_rg[idx], b = list_b[idx];
        if (rd(r, b, buf)) continue;
        if (!ent_ok(r, b, buf)) { logm("refresh: %s %d no esta sano, se omite (usar repair)", R[r].name, b); continue; }
        if (wr(r, b, buf) == 0) done++;
        else logm("refresh: ERROR reescribiendo %s %d", R[r].name, b);
    }
    if (!g_img && !g_dry) { s = fopen(g_state, "w"); if (s) { fprintf(s, "%d\n", idx); fclose(s); } }
    if (g_verbose) logm("refresh: %d bloques reescritos (siguiente %d de %d)", done, idx, tot);
    free(buf);
    return 0;
}

int main(int argc, char **argv)
{
    crc_init();
    int i = 1;
    for (; i < argc && argv[i][0] == '-'; i++) {
        if (!strcmp(argv[i], "-img") && i + 1 < argc) g_img = argv[++i];
        else if (!strcmp(argv[i], "-m") && i + 1 < argc) g_man = argv[++i];
        else if (!strcmp(argv[i], "-v")) g_verbose = 1;
        else if (!strcmp(argv[i], "-n")) g_dry = 1;
    }
    if (i >= argc) {
        fprintf(stderr, "uso: afx [-img imagen -m manifiesto] [-v] [-n] init|check|repair|refresh N|version\n");
        return 1;
    }
    const char *c = argv[i];
    if (!strcmp(c, "version")) { printf("%s (paridad %d, bloque %d)\n", AFX_VER, NPAR, BLK); return 0; }
    if (!strcmp(c, "init")) return cmd_init();
    if (!strcmp(c, "check")) return cmd_check(0);
    if (!strcmp(c, "repair")) return cmd_check(1);
    if (!strcmp(c, "refresh")) return cmd_refresh(i + 1 < argc ? atoi(argv[i + 1]) : 8);
    fprintf(stderr, "comando desconocido: %s\n", c);
    return 1;
}
