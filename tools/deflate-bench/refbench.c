/* In-memory reference timings through the public APIs of libdeflate and zlib-ng, as a black box.
 *
 *   refbench libdeflate enc <level> <file> <reps>   one-shot gzip compression (libdeflate_gzip_compress)
 *   refbench zlibng     enc <level> <file> <reps>   streaming gzip compression in 64 KiB pieces (zng_deflate)
 *   refbench libdeflate dec <gzfile> <reps>         gzip decompression into a buffer of the stored size
 *   refbench zlibng     dec <gzfile> <reps>         the same with zng_inflate
 *
 * The file is read into memory first; only the codec call is timed. Output: size, best seconds, MB/s of the uncompressed size.
 * build.sh compiles it against the pinned libraries.
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <libdeflate.h>
#include <zlib-ng.h>

static double now(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return t.tv_sec + t.tv_nsec * 1e-9; }

static unsigned char *load(const char *path, size_t *n) {
  FILE *f = fopen(path, "rb");
  if (!f) { perror(path); exit(1); }
  fseek(f, 0, SEEK_END); *n = (size_t)ftell(f); fseek(f, 0, SEEK_SET);
  unsigned char *b = malloc(*n ? *n : 1);
  if (fread(b, 1, *n, f) != *n) { perror("read"); exit(1); }
  fclose(f);
  return b;
}

int main(int argc, char **argv) {
  if (argc < 5) { fprintf(stderr, "usage: refbench libdeflate|zlibng enc LEVEL FILE REPS | dec FILE REPS [OUT]\n"); return 2; }
  int ld = strcmp(argv[1], "libdeflate") == 0;
  int enc = strcmp(argv[2], "enc") == 0;
  size_t n; double best = 1e9; size_t out_size = 0, uncompressed = 0;
  if (enc) {
    int level = atoi(argv[3]); int reps = atoi(argv[5]);
    unsigned char *in = load(argv[4], &n); uncompressed = n;
    size_t cap = n + n / 100 + 4096; unsigned char *out = malloc(cap);
    for (int r = -2; r < reps; r++) {
      double t = now();
      if (ld) {
        struct libdeflate_compressor *c = libdeflate_alloc_compressor(level);
        out_size = libdeflate_gzip_compress(c, in, n, out, cap);
        libdeflate_free_compressor(c);
      } else {
        zng_stream s; memset(&s, 0, sizeof s);
        zng_deflateInit2(&s, level, Z_DEFLATED, 31, 8, Z_DEFAULT_STRATEGY);
        s.next_out = out; s.avail_out = (uint32_t)cap; size_t off = 0;
        while (off < n) {
          size_t take = n - off < 65536 ? n - off : 65536;
          s.next_in = in + off; s.avail_in = (uint32_t)take; off += take;
          zng_deflate(&s, off == n ? Z_FINISH : Z_NO_FLUSH);
        }
        out_size = s.total_out; zng_deflateEnd(&s);
      }
      double d = now() - t;
      if (r >= 0 && d < best) best = d;
    }
    if (argc > 6) { FILE *f = fopen(argv[6], "wb"); fwrite(out, 1, out_size, f); fclose(f); }
  } else {
    int reps = atoi(argv[4]);
    unsigned char *in = load(argv[3], &n);
    uncompressed = in[n - 4] | (in[n - 3] << 8) | (in[n - 2] << 16) | ((size_t)in[n - 1] << 24);
    unsigned char *out = malloc(uncompressed + 1); out_size = n;
    for (int r = -2; r < reps; r++) {
      double t = now(); size_t got = 0;
      if (ld) {
        struct libdeflate_decompressor *d = libdeflate_alloc_decompressor();
        if (libdeflate_gzip_decompress(d, in, n, out, uncompressed, &got) != LIBDEFLATE_SUCCESS) { fprintf(stderr, "decode failed\n"); return 1; }
        libdeflate_free_decompressor(d);
      } else {
        zng_stream s; memset(&s, 0, sizeof s);
        zng_inflateInit2(&s, 31);
        s.next_in = in; s.avail_in = (uint32_t)n; s.next_out = out; s.avail_out = (uint32_t)uncompressed;
        if (zng_inflate(&s, Z_FINISH) != Z_STREAM_END) { fprintf(stderr, "decode failed\n"); return 1; }
        zng_inflateEnd(&s);
      }
      double d = now() - t;
      if (r >= 0 && d < best) best = d;
    }
  }
  printf("ref size=%zu seconds=%.4f MBps=%.1f\n", out_size, best, uncompressed / 1e6 / best);
  return 0;
}
