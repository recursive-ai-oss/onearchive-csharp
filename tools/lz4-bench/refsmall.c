// Times the lz4 library's frame compressor on many small payloads, each compressed on its own as a frame with an optional
// prepared dictionary, the way a server compresses per-tick messages. It is the reference side of `Lz4Bench small`, driving
// the installed shared library as a black box through its documented frame API (declarations below follow lz4frame.h's docs).
//
//   cc -O2 refsmall.c -o refsmall /usr/lib/x86_64-linux-gnu/liblz4.so.1
//   refsmall <level> <dictionary|-> <directory> [seconds]
//
// Level is the lz4 compression level (negative for --fast=N). No checksums, no content size, independent 64 KiB blocks.
// Prints the payload count, input and output bytes, and the best payloads per second over repeated passes.
#include <dirent.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

typedef struct LZ4F_cctx_s LZ4F_cctx;
typedef struct LZ4F_CDict_s LZ4F_CDict;
typedef struct {
  int blockSizeID, blockMode, contentChecksumFlag, frameType;
  unsigned long long contentSize;
  unsigned dictID;
  int blockChecksumFlag;
} LZ4F_frameInfo_t;
typedef struct {
  LZ4F_frameInfo_t frameInfo;
  int compressionLevel;
  unsigned autoFlush, favorDecSpeed, reserved[3];
} LZ4F_preferences_t;
size_t LZ4F_createCompressionContext(LZ4F_cctx** ctx, unsigned version);
LZ4F_CDict* LZ4F_createCDict(const void* dict, size_t size);
size_t LZ4F_compressFrame_usingCDict(LZ4F_cctx*, void* dst, size_t cap, const void* src, size_t n, const LZ4F_CDict*, const LZ4F_preferences_t*);
size_t LZ4F_compressFrame(void* dst, size_t cap, const void* src, size_t n, const LZ4F_preferences_t*);

static double now(void) {
  struct timespec t;
  clock_gettime(CLOCK_MONOTONIC, &t);
  return t.tv_sec + t.tv_nsec * 1e-9;
}

static int cmp(const void* a, const void* b) { return strcmp(*(char* const*)a, *(char* const*)b); }

static char* slurp(const char* path, long* size) {
  FILE* f = fopen(path, "rb");
  if (!f) { perror(path); exit(1); }
  fseek(f, 0, SEEK_END);
  *size = ftell(f);
  fseek(f, 0, SEEK_SET);
  char* data = malloc(*size ? *size : 1);
  if (fread(data, 1, *size, f) != (size_t)*size) { perror("read"); exit(1); }
  fclose(f);
  return data;
}

int main(int argc, char** argv) {
  if (argc < 4) { fprintf(stderr, "usage: refsmall level dict|- dir [seconds]\n"); return 2; }
  int level = atoi(argv[1]);
  double seconds = argc > 4 ? atof(argv[4]) : 3;
  char** names = NULL;
  int count = 0;
  DIR* d = opendir(argv[3]);
  if (!d) { perror(argv[3]); return 1; }
  struct dirent* e;
  while ((e = readdir(d))) {
    if (e->d_name[0] == '.') continue;
    names = realloc(names, sizeof(char*) * (count + 1));
    char path[4096];
    snprintf(path, sizeof path, "%s/%s", argv[3], e->d_name);
    names[count++] = strdup(path);
  }
  closedir(d);
  qsort(names, count, sizeof(char*), cmp);
  char** data = malloc(sizeof(char*) * count);
  long* size = malloc(sizeof(long) * count);
  long in = 0;
  for (int i = 0; i < count; i++) { data[i] = slurp(names[i], &size[i]); in += size[i]; }

  LZ4F_CDict* cdict = NULL;
  if (strcmp(argv[2], "-") != 0) {
    long dictSize;
    char* dict = slurp(argv[2], &dictSize);
    cdict = LZ4F_createCDict(dict, dictSize);
  }
  LZ4F_cctx* cctx = NULL;
  LZ4F_createCompressionContext(&cctx, 100);
  LZ4F_preferences_t prefs;
  memset(&prefs, 0, sizeof prefs);
  prefs.compressionLevel = level;

  char* out = malloc(1 << 20);
  long outBytes = 0;
  double best = 1e30, start = now();
  for (int run = 0; run < 3 || now() - start < seconds; run++) {
    double t = now();
    outBytes = 0;
    for (int i = 0; i < count; i++) {
      size_t n = cdict ? LZ4F_compressFrame_usingCDict(cctx, out, 1 << 20, data[i], size[i], cdict, &prefs)
                       : LZ4F_compressFrame(out, 1 << 20, data[i], size[i], &prefs);
      outBytes += (long)n;
    }
    double dt = now() - t;
    if (dt < best) best = dt;
  }
  printf("ref\tlevel=%d\tpayloads=%d\tin=%ld\tout=%ld\tpayloads/s=%.0f\tMB/s=%.1f\n", level, count, in, outBytes, count / best, in / best / 1e6);
  return 0;
}
