// Reference side of the small-payload rows: the zstd and lz4 libraries' own APIs, driven as a black box, one payload at a
// time with a prepared dictionary and a reused context, the way a game server handles per-tick messages.
//
//   cc -O2 refsmall.c -I<prefix>/include -L<prefix>/lib -llz4 -lzstd -Wl,-rpath,<prefix>/lib -o refsmall
//   refsmall <zstd|lz4> <level> <dictionary|-> <packed payloads> <frames out> [seconds]
//
// Level: the codec's level (for lz4, negative means --fast=N). Payload files are packed as repeated [u32 LE length][bytes].
// zstd uses ZSTD_compress_usingCDict / ZSTD_decompress_usingDDict with one ZSTD_CCtx and ZSTD_DCtx; lz4 uses
// LZ4F_compressFrame_usingCDict and LZ4F_decompress_usingDict with one context each (no checksums, no content size).
// The compressed frames are written to <frames out> in the same packing so the other side decodes the same bytes. Prints one line: key=value pairs, rates as payloads per second (best, median, worst of the timed passes).
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <zstd.h>
#include <lz4frame.h>

static double now(void) {
  struct timespec t;
  clock_gettime(CLOCK_MONOTONIC, &t);
  return t.tv_sec + t.tv_nsec * 1e-9;
}
static int cmpd(const void* a, const void* b) { double x = *(const double*)a, y = *(const double*)b; return x < y ? -1 : x > y; }

static unsigned char* slurp(const char* path, long* size) {
  FILE* f = fopen(path, "rb");
  if (!f) { perror(path); exit(1); }
  fseek(f, 0, SEEK_END);
  *size = ftell(f);
  fseek(f, 0, SEEK_SET);
  unsigned char* data = malloc(*size ? *size : 1);
  if (fread(data, 1, *size, f) != (size_t)*size) { perror("read"); exit(1); }
  fclose(f);
  return data;
}

typedef struct { unsigned char** p; long* n; int count; long total; } Set;

static Set unpack(const char* path) {
  long size;
  unsigned char* raw = slurp(path, &size);
  Set s = {0};
  long at = 0;
  while (at + 4 <= size) {
    unsigned len = raw[at] | raw[at + 1] << 8 | raw[at + 2] << 16 | (unsigned)raw[at + 3] << 24;
    s.p = realloc(s.p, sizeof(char*) * (s.count + 1));
    s.n = realloc(s.n, sizeof(long) * (s.count + 1));
    s.p[s.count] = raw + at + 4;
    s.n[s.count] = len;
    s.total += len;
    s.count++;
    at += 4 + len;
  }
  return s;
}

static void report(const char* what, double* t, int runs, int count) {
  qsort(t, runs, sizeof(double), cmpd);
  printf("\t%s_best=%.0f\t%s_median=%.0f\t%s_min=%.0f", what, count / t[0], what, count / t[runs / 2], what, count / t[runs - 1]);
}

int main(int argc, char** argv) {
  if (argc < 6) { fprintf(stderr, "usage: refsmall zstd|lz4 level dict|- packed frames-out [seconds]\n"); return 2; }
  int isZstd = strcmp(argv[1], "zstd") == 0;
  int level = atoi(argv[2]);
  double seconds = argc > 6 ? atof(argv[6]) : 3;
  Set in = unpack(argv[4]);
  long dictSize = 0;
  unsigned char* dict = NULL;
  if (strcmp(argv[3], "-") != 0) dict = slurp(argv[3], &dictSize);

  size_t cap = 1 << 20;
  unsigned char* out = malloc(cap);
  unsigned char* back = malloc(cap);
  long* csize = malloc(sizeof(long) * in.count);
  unsigned char** cdata = malloc(sizeof(char*) * in.count);
  for (int i = 0; i < in.count; i++) cdata[i] = malloc(cap > 0 ? ZSTD_compressBound(in.n[i]) + 64 : 0);

  ZSTD_CCtx* cctx = ZSTD_createCCtx();
  ZSTD_DCtx* dctx = ZSTD_createDCtx();
  ZSTD_CDict* zc = NULL;
  ZSTD_DDict* zd = NULL;
  LZ4F_cctx* lc = NULL;
  LZ4F_dctx* ld = NULL;
  LZ4F_CDict* lcd = NULL;
  LZ4F_preferences_t prefs;
  memset(&prefs, 0, sizeof prefs);
  prefs.compressionLevel = level;
  if (isZstd) {
    if (dict) { zc = ZSTD_createCDict(dict, dictSize, level); zd = ZSTD_createDDict(dict, dictSize); }
  } else {
    LZ4F_createCompressionContext(&lc, LZ4F_VERSION);
    LZ4F_createDecompressionContext(&ld, LZ4F_VERSION);
    if (dict) lcd = LZ4F_createCDict(dict, dictSize);
  }

  int maxRuns = 2000;
  double* tc = malloc(sizeof(double) * maxRuns);
  double* td = malloc(sizeof(double) * maxRuns);
  long outBytes = 0;
  int runs = 0;
  double start = now();
  for (; runs < 5 || (now() - start < seconds && runs < maxRuns); runs++) {
    double t = now();
    outBytes = 0;
    for (int i = 0; i < in.count; i++) {
      size_t n;
      if (isZstd) n = zc ? ZSTD_compress_usingCDict(cctx, cdata[i], ZSTD_compressBound(in.n[i]), in.p[i], in.n[i], zc)
                         : ZSTD_compressCCtx(cctx, cdata[i], ZSTD_compressBound(in.n[i]), in.p[i], in.n[i], level);
      else n = lcd ? LZ4F_compressFrame_usingCDict(lc, cdata[i], ZSTD_compressBound(in.n[i]) + 64, in.p[i], in.n[i], lcd, &prefs)
                   : LZ4F_compressFrame(cdata[i], ZSTD_compressBound(in.n[i]) + 64, in.p[i], in.n[i], &prefs);
      if ((isZstd && ZSTD_isError(n)) || (!isZstd && LZ4F_isError(n))) { fprintf(stderr, "compress failed\n"); return 1; }
      csize[i] = (long)n;
      outBytes += (long)n;
    }
    tc[runs] = now() - t;
  }
  int cruns = runs;

  start = now();
  for (runs = 0; runs < 5 || (now() - start < seconds && runs < maxRuns); runs++) {
    double t = now();
    for (int i = 0; i < in.count; i++) {
      size_t n;
      if (isZstd) {
        n = zd ? ZSTD_decompress_usingDDict(dctx, back, cap, cdata[i], csize[i], zd) : ZSTD_decompressDCtx(dctx, back, cap, cdata[i], csize[i]);
        if (ZSTD_isError(n)) { fprintf(stderr, "decompress failed\n"); return 1; }
      } else {
        size_t dst = cap, src = csize[i];
        LZ4F_resetDecompressionContext(ld);
        LZ4F_decompressOptions_t o = {0};
        n = dict ? LZ4F_decompress_usingDict(ld, back, &dst, cdata[i], &src, dict, dictSize, &o) : LZ4F_decompress(ld, back, &dst, cdata[i], &src, &o);
        if (LZ4F_isError(n) || n != 0 || src != (size_t)csize[i]) { fprintf(stderr, "decompress failed\n"); return 1; }
        n = dst;
      }
      if ((long)n != in.n[i] || memcmp(back, in.p[i], n) != 0) { fprintf(stderr, "round trip mismatch\n"); return 1; }
    }
    td[runs] = now() - t;
  }

  FILE* f = fopen(argv[5], "wb");
  for (int i = 0; i < in.count; i++) {
    unsigned char h[4] = { csize[i] & 255, csize[i] >> 8 & 255, csize[i] >> 16 & 255, csize[i] >> 24 & 255 };
    fwrite(h, 1, 4, f);
    fwrite(cdata[i], 1, csize[i], f);
  }
  fclose(f);

  printf("ref\t%s\tlevel=%d\tpayloads=%d\tin=%ld\tout=%ld", argv[1], level, in.count, in.total, outBytes);
  report("compress", tc, cruns, in.count);
  report("decompress", td, runs, in.count);
  printf("\n");
  (void)out;
  return 0;
}
