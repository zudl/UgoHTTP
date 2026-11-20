#define _GNU_SOURCE
#include "ugo.h"

#include <psa/crypto.h>

#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <ctype.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

static const char *http_base;
static const char *https_base;
static const char *run_dir;
static const char *tmp_dir;
static char *ca_pem;
static size_t ca_len;
static char failure[512];
static const char *skipped;
static int sequence;

static const char OK_BODY[] = "UgoHTTP fault bench\n";

extern unsigned ugo_test_body_allocations;
extern unsigned ugo_test_ws_partial_sends;
extern unsigned ugo_test_curl_large_allocations;
extern unsigned ugo_test_ca_parses;
extern bool ugo_test_bg_expire_early;
extern bool ugo_test_same_origin(const char *a, const char *b);

#define FAIL(...) do { fail_at(__LINE__, __VA_ARGS__); return; } while(0)
#define CHECK(cond) do { if(!(cond)) FAIL("%s", #cond); } while(0)
#define CHECK_EQ(a, b) do { long long a_ = (long long)(a), b_ = (long long)(b); \
    if(a_ != b_) FAIL("%s == %s (%lld != %lld)", #a, #b, a_, b_); } while(0)

static void fail_at(int line, const char *format, ...)
{
    if(failure[0])
        return;
    int n = snprintf(failure, sizeof(failure), "line %d: ", line);
    va_list args;
    va_start(args, format);
    vsnprintf(failure + n, sizeof(failure) - (size_t)n, format, args);
    va_end(args);
}

static void sleep_ms(int ms)
{
    struct timespec ts = {ms / 1000, (long)(ms % 1000) * 1000000L};
    nanosleep(&ts, NULL);
}

static ugo_bytes text(const char *s)
{
    ugo_bytes b = {(uint64_t)(uintptr_t)s, s ? strlen(s) : 0};
    return b;
}

static ugo_bytes bytes(const void *p, size_t n)
{
    ugo_bytes b = {(uint64_t)(uintptr_t)p, n};
    return b;
}

static int next_tag(void)
{
    return __atomic_add_fetch(&sequence, 1, __ATOMIC_RELAXED);
}

static char *make_url(char *buffer, size_t size, const char *base, const char *path)
{
    snprintf(buffer, size, "%s%s", base, path);
    return buffer;
}

static char *temp_path(char *buffer, size_t size, const char *name)
{
    snprintf(buffer, size, "%s/%d-%s", tmp_dir, next_tag(), name);
    return buffer;
}

static long long file_size(const char *path)
{
    struct stat st;
    return stat(path, &st) ? -1 : (long long)st.st_size;
}

static char *read_file(const char *path, size_t *len)
{
    FILE *f = fopen(path, "rb");
    if(!f)
        return NULL;
    fseek(f, 0, SEEK_END);
    long n = ftell(f);
    fseek(f, 0, SEEK_SET);
    char *data = malloc((size_t)n + 1);
    size_t got = fread(data, 1, (size_t)n, f);
    fclose(f);
    data[got] = 0;
    *len = got;
    return data;
}

static int write_file(const char *path, const void *data, size_t len)
{
    FILE *f = fopen(path, "wb");
    if(!f)
        return -1;
    size_t n = fwrite(data, 1, len, f);
    return fclose(f) || n != len ? -1 : 0;
}

/* SHA-256 (FIPS 180-4), only to compare uploaded bytes with the bench's record. */
static const uint32_t K[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};

#define ROR(x, n) (((x) >> (n)) | ((x) << (32 - (n))))

static void sha_block(uint32_t h[8], const uint8_t *p)
{
    uint32_t w[64];
    for(int i = 0; i < 16; ++i)
        w[i] = (uint32_t)p[i * 4] << 24 | (uint32_t)p[i * 4 + 1] << 16 | (uint32_t)p[i * 4 + 2] << 8 | p[i * 4 + 3];
    for(int i = 16; i < 64; ++i) {
        uint32_t s0 = ROR(w[i - 15], 7) ^ ROR(w[i - 15], 18) ^ (w[i - 15] >> 3);
        uint32_t s1 = ROR(w[i - 2], 17) ^ ROR(w[i - 2], 19) ^ (w[i - 2] >> 10);
        w[i] = w[i - 16] + s0 + w[i - 7] + s1;
    }
    uint32_t a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], k = h[7];
    for(int i = 0; i < 64; ++i) {
        uint32_t t1 = k + (ROR(e, 6) ^ ROR(e, 11) ^ ROR(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + w[i];
        uint32_t t2 = (ROR(a, 2) ^ ROR(a, 13) ^ ROR(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
        k = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
    }
    h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += k;
}

static void sha256_hex(const void *data, size_t len, char out[65])
{
    uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    const uint8_t *p = data;
    size_t left = len;
    for(; left >= 64; left -= 64, p += 64)
        sha_block(h, p);
    uint8_t tail[128] = {0};
    memcpy(tail, p, left);
    tail[left] = 0x80;
    size_t blocks = left + 9 > 64 ? 2 : 1;
    uint64_t bits = (uint64_t)len * 8;
    for(int i = 0; i < 8; ++i)
        tail[blocks * 64 - 1 - i] = (uint8_t)(bits >> (i * 8));
    for(size_t i = 0; i < blocks; ++i)
        sha_block(h, tail + i * 64);
    for(int i = 0; i < 8; ++i)
        snprintf(out + i * 8, 9, "%08x", h[i]);
}

static void recorded_sha(const char *target, char out[65])
{
    out[0] = 0;
    char path[1024], needle[600];
    snprintf(path, sizeof(path), "%s/requests.jsonl", run_dir);
    snprintf(needle, sizeof(needle), "\"target\": \"%s\"", target);
    size_t len;
    char *log = read_file(path, &len);
    if(!log)
        return;
    char *hit = strstr(log, needle);
    if(hit) {
        char *sha = strstr(hit, "\"body_sha256\": \"");
        if(sha)
            memcpy(out, sha + 16, 64), out[64] = 0;
    }
    free(log);
}

static char *recorded(const char *target)
{
    char path[1024], needle[600];
    snprintf(path, sizeof(path), "%s/requests.jsonl", run_dir);
    snprintf(needle, sizeof(needle), "\"target\": \"%s\"", target);
    size_t len;
    char *log = read_file(path, &len);
    if(!log)
        return NULL;
    char *line = NULL;
    char *hit = strstr(log, needle);
    if(hit) {
        char *start = hit;
        while(start > log && start[-1] != '\n')
            --start;
        char *end = strchr(hit, '\n');
        size_t n = end ? (size_t)(end - start) : strlen(start);
        line = malloc(n + 1);
        memcpy(line, start, n);
        line[n] = 0;
    }
    free(log);
    return line;
}

static int compare_names(const void *a, const void *b)
{
    return strcmp((const char *)a, (const char *)b);
}

static void header_names(const char *target, char *out, size_t size)
{
    out[0] = 0;
    char *line = recorded(target);
    const char *p = line ? strstr(line, "\"headers\": [") : NULL;
    char names[16][64];
    size_t count = 0;
    if(p)
        p += 12;
    while(p && *p == '"' && count < 16) {
        const char *start = p + 1;
        const char *end = strchr(start, '"');
        const char *colon = strchr(start + (*start == ':'), ':');
        if(!end)
            break;
        size_t n = (size_t)((colon && colon < end ? colon : end) - start);
        if(n >= sizeof(names[0]))
            n = sizeof(names[0]) - 1;
        for(size_t i = 0; i < n; ++i)
            names[count][i] = (char)(start[i] >= 'A' && start[i] <= 'Z' ? start[i] + 32 : start[i]);
        names[count++][n] = 0;
        p = end + 1;
        if(!strncmp(p, ", ", 2))
            p += 2;
    }
    qsort(names, count, sizeof(names[0]), compare_names);
    for(size_t i = 0; i < count; ++i) {
        size_t used = strlen(out);
        snprintf(out + used, size - used, "%s%s", i ? "," : "", names[i]);
    }
    free(line);
}

static ugo_client *client_new(uint32_t max)
{
    ugo_client_config config = {0};
    config.abi_version = UGO_ABI_VERSION;
    config.max_transfers = max;
    config.ca_bundle = bytes(ca_pem, ca_len);
    ugo_client *c = NULL;
    return ugo_client_create(&config, &c) == UGO_OK ? c : NULL;
}

static ugo_request get_request(const char *url)
{
    ugo_request r;
    memset(&r, 0, sizeof(r));
    r.method = text("GET");
    r.url = text(url);
    r.memory_limit = 1 << 20;
    r.connect_timeout_ms = 5000;
    r.total_timeout_ms = 30000;
    r.max_redirects = 5;
    return r;
}

typedef struct {
    uint64_t id;
    int headers;
    int progress;
    int completed;
    int late;
    int64_t last_us;
    ugo_ev_headers h;
    ugo_ev_progress p;
    ugo_ev_completed done;
} track;

static int apply(track *t, int n, const ugo_event *e)
{
    for(int i = 0; i < n; ++i) {
        if(t[i].id != e->id)
            continue;
        if(t[i].completed || e->time_us < t[i].last_us)
            t[i].late++;
        t[i].last_us = e->time_us;
        if(e->kind == UGO_EV_HEADERS) {
            if(t[i].headers++)
                t[i].late++;
            t[i].h = e->u.headers;
        } else if(e->kind == UGO_EV_PROGRESS) {
            t[i].progress++;
            t[i].p = e->u.progress;
        } else if(e->kind == UGO_EV_COMPLETED) {
            t[i].completed++;
            t[i].done = e->u.completed;
        } else {
            t[i].late++;
        }
        return 1;
    }
    return 0;
}

static int drain(ugo_client *c, track *t, int n, int timeout_ms)
{
    int64_t end = ugo_monotonic_us() + (int64_t)timeout_ms * 1000;
    for(;;) {
        int all = 1;
        for(int i = 0; i < n; ++i)
            all &= t[i].completed > 0;
        if(all)
            return 1;
        if(ugo_monotonic_us() > end)
            return 0;
        ugo_event events[8];
        int32_t k = ugo_wait_events(c, events, 8, 100);
        if(k < 0)
            return 0;
        for(int i = 0; i < k; ++i)
            if(!apply(t, n, &events[i]))
                return 0;
    }
}

static int quiet(ugo_client *c, int ms)
{
    ugo_event events[4];
    return ugo_wait_events(c, events, 4, (uint32_t)ms) == 0;
}

static int run_one(ugo_client *c, const ugo_request *r, track *t)
{
    memset(t, 0, sizeof(*t));
    if(ugo_submit(c, r, &t->id) != UGO_OK)
        return 0;
    return drain(c, t, 1, 20000);
}

static void test_abi_and_config(void)
{
    ugo_client_config config = {0};
    ugo_client *c = NULL;
    config.abi_version = UGO_ABI_VERSION + 1;
    config.max_transfers = 4;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_ABI);
    config.abi_version = 0;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_ABI);
    config.abi_version = UGO_ABI_VERSION;
    config.max_transfers = 0;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_INVALID_ARG);
    config.max_transfers = 4;
    config.reserved = 1;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_INVALID_ARG);
    config.reserved = 0;
    config.flags = UGO_CLIENT_TLS_INSECURE_DEV;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_INVALID_ARG);
    config.flags = 1u << 5;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_INVALID_ARG);
    config.flags = 0;
    config.ca_bundle.len = 10;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_client_create(NULL, &c), UGO_E_INVALID_ARG);
    CHECK(c == NULL);
    config.ca_bundle.len = 0;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_OK);
    CHECK(c != NULL);
    ugo_event e;
    CHECK_EQ(ugo_wait_events(c, &e, 0, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_wait_events(c, &e, 1, 0), 0);
    ugo_client_destroy(c);
}

static void test_monotonic(void)
{
    int64_t a = ugo_monotonic_us();
    sleep_ms(20);
    int64_t b = ugo_monotonic_us();
    CHECK(a > 0);
    CHECK(b - a >= 15000);
    CHECK(b - a < 2000000);
}

static void test_get_memory(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.h.http_status, 200);
    CHECK_EQ(t.h.http_version, UGO_HTTP_1_1);
    CHECK_EQ(t.h.content_length, (long long)strlen(OK_BODY));
    CHECK(t.h.headers.len > 12 && !memcmp((const void *)(uintptr_t)t.h.headers.ptr, "HTTP/1.1 200", 12));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_status, 200);
    CHECK_EQ(t.done.http_version, UGO_HTTP_1_1);
    CHECK_EQ(t.done.bytes_received, strlen(OK_BODY));
    CHECK(t.done.total_us > 0);
    CHECK(t.done.connect_us > 0);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, strlen(OK_BODY));
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, OK_BODY, res.body.len));
    CHECK_EQ(res.headers.len, t.h.headers.len);
    CHECK_EQ(res.headers.ptr, t.h.headers.ptr);
    const char *h = (const char *)(uintptr_t)res.headers.ptr;
    CHECK(!memcmp(h + res.headers.len - 4, "\r\n\r\n", 4));
    CHECK_EQ(res.error_message.len, 0);
    CHECK(quiet(c, 100));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    CHECK_EQ(ugo_release(c, t.id), UGO_E_STALE_ID);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_E_STALE_ID);
    CHECK_EQ(ugo_abort(c, t.id), UGO_E_STALE_ID);
    CHECK_EQ(ugo_abort(c, 0), UGO_E_STALE_ID);
    CHECK_EQ(ugo_abort(c, (uint64_t)1 << 32 | 99), UGO_E_STALE_ID);
    track t2;
    CHECK(run_one(c, &r, &t2));
    CHECK(t2.id != t.id);
    CHECK_EQ((uint32_t)t2.id, (uint32_t)t.id);
    CHECK_EQ(t2.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t2.id), UGO_OK);
    ugo_client_shutdown(c);
    ugo_client_destroy(c);
}

static void test_https_h2(void)
{
    ugo_client *c = client_new(8);
    CHECK(c);
    char url[512];
    make_url(url, sizeof(url), https_base, "/big/300000");
    track t[5];
    memset(t, 0, sizeof(t));
    for(int i = 0; i < 5; ++i) {
        ugo_request r = get_request(url);
        CHECK_EQ(ugo_submit(c, &r, &t[i].id), UGO_OK);
    }
    CHECK(drain(c, t, 5, 20000));
    for(int i = 0; i < 5; ++i) {
        CHECK_EQ(t[i].late, 0);
        CHECK_EQ(t[i].done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(t[i].done.http_version, UGO_HTTP_2);
        CHECK_EQ(t[i].h.http_version, UGO_HTTP_2);
        CHECK_EQ(t[i].done.bytes_received, 300000);
        ugo_result res;
        CHECK_EQ(ugo_get_result(c, t[i].id, &res), UGO_OK);
        CHECK_EQ(res.body.len, 300000);
        const uint8_t *b = (const uint8_t *)(uintptr_t)res.body.ptr;
        for(int k = 0; k < 300000; k += 997)
            CHECK_EQ(b[k], k % 256);
        CHECK_EQ(ugo_release(c, t[i].id), UGO_OK);
    }
    make_url(url, sizeof(url), https_base, "/ok");
    ugo_request r = get_request(url);
    track again;
    CHECK(run_one(c, &r, &again));
    CHECK_EQ(again.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(again.done.connection_reused, 1);
    CHECK(again.done.appconnect_us == 0 || again.done.appconnect_us < again.done.total_us);
    ugo_client_destroy(c);
}

static void test_https1_parallel_not_queued(void)
{
    const char *base = getenv("UGO_TEST_HTTPS1");
    CHECK(base);
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), base, "/stall-after-headers"));
    track t[3];
    memset(t, 0, sizeof(t));
    for(int i = 0; i < 3; ++i)
        CHECK_EQ(ugo_submit(c, &r, &t[i].id), UGO_OK);
    int64_t deadline = ugo_monotonic_us() + 5000000;
    ugo_event e[8];
    while(!(t[0].headers && t[1].headers && t[2].headers) && ugo_monotonic_us() < deadline) {
        int32_t n = ugo_wait_events(c, e, 8, 100);
        CHECK(n >= 0);
        for(int32_t i = 0; i < n; ++i) {
            CHECK(e[i].kind != UGO_EV_COMPLETED);
            CHECK(apply(t, 3, &e[i]));
        }
    }
    for(int i = 0; i < 3; ++i) {
        CHECK_EQ(t[i].headers, 1);
        CHECK_EQ(t[i].late, 0);
        CHECK_EQ(t[i].h.http_version, UGO_HTTP_1_1);
    }
    ugo_client_destroy(c);
}

static unsigned ca_parses(void)
{
    return __atomic_load_n(&ugo_test_ca_parses, __ATOMIC_RELAXED);
}

static void test_tls_failure(void)
{
    char path[1024];
    snprintf(path, sizeof(path), "%s/self-signed.pem", run_dir);
    size_t len;
    char *other = read_file(path, &len);
    CHECK(other);
    const char *garbage = "-----BEGIN CERTIFICATE-----\nnot base64\n-----END CERTIFICATE-----\n";
    size_t garbage_len = strlen(garbage);
    char *partial = malloc(ca_len + garbage_len);
    CHECK(partial);
    memcpy(partial, ca_pem, ca_len);
    memcpy(partial + ca_len, garbage, garbage_len);
    const ugo_bytes cas[] = {bytes(other, len), text(garbage), bytes(partial, ca_len + garbage_len)};
    const int32_t codes[] = {60, 58, 58};
    const char *rejected = "the CA bundle has a certificate mbedTLS cannot parse";
    for(int i = 0; i < 3 && !failure[0]; ++i) {
        ugo_client_config config = {0};
        config.abi_version = UGO_ABI_VERSION;
        config.max_transfers = 2;
        config.ca_bundle = cas[i];
        ugo_client *c = NULL;
        CHECK_EQ(ugo_client_create(&config, &c), UGO_OK);
        unsigned parses = ca_parses();
        char url[512];
        ugo_request r = get_request(make_url(url, sizeof(url), https_base, "/ok"));
        for(int round = 0; round < 2 && !failure[0]; ++round) {
            track t;
            int ok = run_one(c, &r, &t);
            ugo_result res;
            int32_t got = ugo_get_result(c, t.id, &res);
            const char *message = (const char *)(uintptr_t)res.error_message.ptr;
            if(!ok || got != UGO_OK || t.done.error_class != UGO_CLASS_TLS || t.done.curl_code != codes[i] || t.headers)
                FAIL("ca %d: class %u curl %d", i, t.done.error_class, t.done.curl_code);
            else if(codes[i] == 58 && (res.error_message.len != strlen(rejected) || memcmp(message, rejected, strlen(rejected))))
                FAIL("ca %d: message %.*s", i, (int)res.error_message.len, message);
            ugo_release(c, t.id);
        }
        if(ca_parses() - parses != (codes[i] == 58 ? 2u : 1u))
            FAIL("ca %d: parsed %u times", i, ca_parses() - parses);
        ugo_client_destroy(c);
    }
    free(partial);
    free(other);
}

static void check_ca(int i, ugo_bytes ca)
{
    ugo_client_config config = {0};
    config.abi_version = UGO_ABI_VERSION;
    config.max_transfers = 1;
    config.ca_bundle = ca;
    ugo_client *c = NULL;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_OK);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), https_base, "/ok"));
    unsigned before = __atomic_load_n(&ugo_test_curl_large_allocations, __ATOMIC_RELAXED);
    unsigned parses = ca_parses();
    track t;
    int ok = run_one(c, &r, &t);
    unsigned large = __atomic_load_n(&ugo_test_curl_large_allocations, __ATOMIC_RELAXED) - before;
    ugo_client_destroy(c);
    CHECK(ok);
    if(ca.len) {
        if(t.done.error_class != UGO_CLASS_NONE || t.done.http_status != 200)
            FAIL("ca %d: class %u curl %d status %u", i, t.done.error_class, t.done.curl_code, t.done.http_status);
    } else if(t.done.error_class != UGO_CLASS_TLS || t.done.curl_code != 60) {
        /* The built-in bundle lacks the bench CA: peer verification must fail, not loading the bundle. */
        FAIL("built-in ca: class %u curl %d", t.done.error_class, t.done.curl_code);
    }
    if(large || ca_parses() - parses != 1)
        FAIL("ca %d: %u large curl allocations, %u parses", i, large, ca_parses() - parses);
}

static void test_ca_bundle_not_copied(void)
{
    /* The PEM parser skips text before a certificate; the padding makes the bundle as large as the built-in one. */
    size_t pad = 200000;
    char *big = malloc(pad + ca_len + 1);
    CHECK(big);
    for(size_t i = 0; i < pad; i += 100) {
        memset(big + i, '#', 99);
        big[i + 99] = '\n';
    }
    memcpy(big + pad, ca_pem, ca_len + 1);
    const ugo_bytes cas[] = {bytes(ca_pem, ca_len), bytes(ca_pem, ca_len + 1), bytes(big, pad + ca_len),
                             bytes(big, pad + ca_len + 1), bytes(NULL, 0)};
    for(int i = 0; i < 5 && !failure[0]; ++i)
        check_ca(i, cas[i]);
    free(big);
}

static void test_ca_parsed_once(void)
{
    const char *https1 = getenv("UGO_TEST_HTTPS1");
    CHECK(https1);
    enum { PARALLEL = 8 };
    ugo_client *c = client_new(PARALLEL * 2);
    CHECK(c);
    unsigned parses = ca_parses();
    char close_url[512], h2_url[512];
    /* The bench closes HTTP/1.1 connections after /chunked: every request needs a new handshake. */
    ugo_request close_r = get_request(make_url(close_url, sizeof(close_url), https1, "/chunked"));
    ugo_request h2_r = get_request(make_url(h2_url, sizeof(h2_url), https_base, "/ok"));
    for(int i = 0; i < 10 && !failure[0]; ++i) {
        track t;
        CHECK(run_one(c, &close_r, &t));
        if(t.done.error_class != UGO_CLASS_NONE || t.done.http_status != 200 || t.done.connection_reused)
            FAIL("sequential %d: class %u curl %d reused %u", i, t.done.error_class, t.done.curl_code,
                 t.done.connection_reused);
        ugo_release(c, t.id);
    }
    track t[PARALLEL * 2];
    memset(t, 0, sizeof(t));
    for(int i = 0; i < PARALLEL * 2; ++i)
        CHECK_EQ(ugo_submit(c, i < PARALLEL ? &close_r : &h2_r, &t[i].id), UGO_OK);
    CHECK(drain(c, t, PARALLEL * 2, 20000));
    for(int i = 0; i < PARALLEL * 2 && !failure[0]; ++i)
        if(t[i].done.error_class != UGO_CLASS_NONE || t[i].done.http_status != 200)
            FAIL("parallel %d: class %u curl %d", i, t[i].done.error_class, t[i].done.curl_code);
    ugo_client_destroy(c);
    if(!failure[0] && ca_parses() - parses != 1)
        FAIL("parsed %u times", ca_parses() - parses);
}

static void test_get_file(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/big/200000"));
    r.sink_path = text(temp_path(path, sizeof(path), "get.part"));
    r.memory_limit = 0;
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(file_size(path), 200000);
    size_t len;
    uint8_t *data = (uint8_t *)read_file(path, &len);
    CHECK(data);
    int same = 1;
    for(size_t i = 0; i < len; ++i)
        same &= data[i] == (uint8_t)(i % 256);
    free(data);
    CHECK(same);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 0);
    CHECK(res.headers.len > 0);
    ugo_client_destroy(c);
    unlink(path);
}

static void test_non2xx(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/status/404"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.http_status, 404);
    CHECK_EQ(t.h.http_status, 404);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, strlen(OK_BODY));
    CHECK(res.error_message.len > 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.sink_path = text(temp_path(path, sizeof(path), "404.part"));
    r.flags = UGO_REQ_KEEP_PARTIAL;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
    CHECK_EQ(file_size(path), -1);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, strlen(OK_BODY));
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, OK_BODY, res.body.len));
    ugo_client_destroy(c);
}

static void test_empty_2xx_file(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    temp_path(path, sizeof(path), "204.part");
    CHECK_EQ(write_file(path, "stale", 5), 0);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/status/204"));
    r.sink_path = text(path);
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 204);
    CHECK_EQ(file_size(path), 0);
    ugo_client_destroy(c);
    unlink(path);
}

static void test_cut_body_file(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/cut-mid-body"));
    r.sink_path = text(temp_path(path, sizeof(path), "cut.part"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(file_size(path), -1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r.flags = UGO_REQ_KEEP_PARTIAL;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(file_size(path), (long long)strlen(OK_BODY) / 2);
    ugo_client_destroy(c);
    unlink(path);
}

static int write_range_prefix(const char *path, size_t len, int version);
static int range_file_is(const char *path, size_t size, int version);

static void test_abort_file(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/slow-body/200000?bps=40000"));
    r.sink_path = text(temp_path(path, sizeof(path), "abort.part"));
    for(int keep = 0; keep < 2; ++keep) {
        r.flags = keep ? UGO_REQ_KEEP_PARTIAL : 0;
        track t = {0};
        CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
        int64_t end = ugo_monotonic_us() + 5000000;
        while(file_size(path) <= 0 && ugo_monotonic_us() < end)
            sleep_ms(5);
        CHECK(file_size(path) > 0);
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK(drain(c, &t, 1, 5000));
        CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
        if(keep)
            CHECK(file_size(path) > 0 && file_size(path) < 200000);
        else
            CHECK_EQ(file_size(path), -1);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    write_range_prefix(path, 1000, 0);
    track t = {0};
    r.url = text(make_url(url, sizeof(url), http_base, "/range/200000?bps=40000"));
    r.flags = UGO_REQ_RESUME;
    CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
    int64_t end = ugo_monotonic_us() + 5000000;
    while(file_size(path) <= 1000 && ugo_monotonic_us() < end)
        sleep_ms(5);
    CHECK(file_size(path) > 1000);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(drain(c, &t, 1, 5000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.h.http_status, 206);
    long long kept = file_size(path);
    CHECK(kept > 1000 && kept < 200000);
    CHECK(range_file_is(path, (size_t)kept, 0));
    unlink(path);
    ugo_client_destroy(c);
}

static void test_disk_full(void)
{
    const char *dir = getenv("UGO_TEST_FULL_DISK");
    if(!dir) {
        skipped = "UGO_TEST_FULL_DISK unset (needs a small tmpfs)";
        return;
    }
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[512];
    snprintf(path, sizeof(path), "%s/%d-full.part", dir, next_tag());
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/big/3000000"));
    r.sink_path = text(path);
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
    CHECK_EQ(file_size(path), -1);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(res.error_message.len > 0);
    ugo_client_destroy(c);
}

static void test_size_overflow(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    static const uint8_t byte = 0;
    ugo_mime_part parts[2];
    memset(parts, 0, sizeof(parts));
    for(int i = 0; i < 2; ++i) {
        parts[i].name = text("p");
        parts[i].source = UGO_PART_MEMORY;
        parts[i].data = bytes(&byte, SIZE_MAX / 2);
    }
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MULTIPART;
    r.parts = (uint64_t)(uintptr_t)parts;
    r.part_count = 2;
    r.flags = UGO_REQ_DETACHED;
    r.memory_limit = 0;
    uint64_t id = 7;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(id, 7);
    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ((uint32_t)t.id, 0);
    CHECK_EQ(t.id >> 32, 1);
    ugo_client_destroy(c);
}

static void test_fifo(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], fifo[512];
    temp_path(fifo, sizeof(fifo), "fifo");
    CHECK_EQ(mkfifo(fifo, 0600), 0);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_FILE;
    r.body = text(fifo);
    track t;
    int64_t start = ugo_monotonic_us();
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_mime_part part;
    memset(&part, 0, sizeof(part));
    part.name = text("file");
    part.source = UGO_PART_FILE;
    part.data = text(fifo);
    r.body_kind = UGO_BODY_MULTIPART;
    r.body = text(NULL);
    r.parts = (uint64_t)(uintptr_t)&part;
    r.part_count = 1;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    for(int reader = 0; reader < 2; ++reader) {
        int fd = reader ? open(fifo, O_RDONLY | O_NONBLOCK) : -1;
        CHECK(!reader || fd >= 0);
        r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
        r.sink_path = text(fifo);
        CHECK(run_one(c, &r, &t));
        if(fd >= 0)
            close(fd);
        CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        struct stat st;
        CHECK(!stat(fifo, &st) && S_ISFIFO(st.st_mode));
    }
    CHECK(ugo_monotonic_us() - start < 5000000);
    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_FILE;
    r.body = text(fifo);
    CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
    start = ugo_monotonic_us();
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    CHECK(ugo_monotonic_us() - start < 2000000);
    ugo_client_destroy(c);
    unlink(fifo);
}

static void test_partial_not_deleted(void)
{
    if(geteuid() == 0) {
        skipped = "root ignores directory permissions";
        return;
    }
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], dir[512], path[600];
    temp_path(dir, sizeof(dir), "locked");
    CHECK_EQ(mkdir(dir, 0700), 0);
    snprintf(path, sizeof(path), "%s/x.part", dir);
    CHECK_EQ(write_file(path, "old", 3), 0);
    CHECK_EQ(chmod(dir, 0500), 0);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/cut-mid-body"));
    r.sink_path = text(path);
    track t;
    int ok = run_one(c, &r, &t);
    ugo_result res;
    int32_t got = ugo_get_result(c, t.id, &res);
    chmod(dir, 0700);
    long long left = file_size(path);
    unlink(path);
    rmdir(dir);
    CHECK(ok);
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(t.done.curl_code, 18);
    CHECK_EQ(left, (long long)strlen(OK_BODY) / 2);
    CHECK_EQ(got, UGO_OK);
    CHECK(res.error_message.len && memmem((const void *)(uintptr_t)res.error_message.ptr, res.error_message.len,
                                          "could not be deleted", 20));
    ugo_client_destroy(c);
}

static void test_body_allocations(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/big/300000"));
    track t;
    unsigned before = __atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(__atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED) - before, 1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r.memory_limit = 100000;
    before = __atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    CHECK_EQ(__atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED) - before, 1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), https_base, "/big/300000"));
    before = __atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.http_version, UGO_HTTP_2);
    CHECK_EQ(__atomic_load_n(&ugo_test_body_allocations, __ATOMIC_RELAXED) - before, 1);
    ugo_client_destroy(c);
}

static void test_sink_local_io(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    snprintf(path, sizeof(path), "%s/missing-dir/x.part", tmp_dir);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.sink_path = text(path);
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/status/204"));
    r.sink_path = text(path);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_FILE;
    r.body = text(path);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_LOCAL_IO);
    ugo_client_destroy(c);
}

static void test_memory_limit(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/big/100000"));
    r.memory_limit = 1000;
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.memory_limit = strlen(OK_BODY);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/chunked"));
    r.memory_limit = strlen(OK_BODY) - 1;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    ugo_client_destroy(c);
}

static void check_upload(ugo_client *c, ugo_request *r, const char *path_fmt, const void *body, size_t len)
{
    char url[512], target[256], sha[65], want[65];
    snprintf(target, sizeof(target), path_fmt, next_tag());
    r->url = text(make_url(url, sizeof(url), http_base, target));
    track t;
    CHECK(run_one(c, r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.bytes_sent, len);
    recorded_sha(target, sha);
    sha256_hex(body, len, want);
    if(strcmp(sha, want))
        FAIL("%s: recorded %s, sent %s", target, sha, want);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
}

static uint8_t *pattern(size_t len)
{
    uint8_t *p = malloc(len);
    for(size_t i = 0; i < len; ++i)
        p[i] = (uint8_t)(i * 31 + 7);
    return p;
}

static void test_post(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    ugo_request r = get_request("");
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("{\"hello\":1}");
    check_upload(c, &r, "/ok?t=%d", "{\"hello\":1}", 11);
    CHECK(!failure[0]);
    r.body_kind = UGO_BODY_NONE;
    r.body = text(NULL);
    check_upload(c, &r, "/ok?t=%d", "", 0);
    CHECK(!failure[0]);
    size_t n = 300000;
    uint8_t *data = pattern(n);
    char path[512];
    temp_path(path, sizeof(path), "upload.bin");
    CHECK_EQ(write_file(path, data, n), 0);
    r.body_kind = UGO_BODY_FILE;
    r.body = text(path);
    check_upload(c, &r, "/ok?t=%d", data, n);
    r.body_kind = UGO_BODY_MEMORY;
    r.body = bytes(data, n);
    check_upload(c, &r, "/ok?t=%d", data, n);
    free(data);
    unlink(path);
    ugo_client_destroy(c);
}

static void redirect_case(ugo_client *c, ugo_request *r, int code, const char *expect_sha)
{
    char url[512], first[256], second[128], sha1[65], sha2[65];
    int tag = next_tag();
    snprintf(second, sizeof(second), "/ok?t=%d", tag);
    snprintf(first, sizeof(first), "/redirect/%d?to=%%2Fok%%3Ft%%3D%d", code, tag);
    r->url = text(make_url(url, sizeof(url), http_base, first));
    track t;
    CHECK(run_one(c, r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 1);
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.h.http_status, 200);
    recorded_sha(first, sha1);
    recorded_sha(second, sha2);
    CHECK(sha1[0] && sha2[0]);
    char *hop = recorded(second), want[64];
    snprintf(want, sizeof(want), "\"method\": \"%.*s\"", (int)r->method.len, (const char *)(uintptr_t)r->method.ptr);
    bool same_method = hop && strstr(hop, want);
    free(hop);
    if(!same_method)
        FAIL("redirect %d: method not replayed", code);
    if(strcmp(sha1, sha2))
        FAIL("redirect %d: %s != %s", code, sha1, sha2);
    if(expect_sha && strcmp(sha1, expect_sha))
        FAIL("redirect %d: sent %s, expected %s", code, sha1, expect_sha);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(!memcmp((const char *)(uintptr_t)res.headers.ptr, "HTTP/1.1 200", 12));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
}

static void test_redirect_replay(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char sha[65], empty[65], path[512];
    sha256_hex("", 0, empty);
    const char *json = "{\"signed\":\"payload\"}";
    sha256_hex(json, strlen(json), sha);
    ugo_request r = get_request("");
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text(json);
    int codes[] = {301, 302, 307, 308};
    for(int i = 0; i < 4; ++i) {
        redirect_case(c, &r, codes[i], sha);
        CHECK(!failure[0]);
    }
    r.method = text("PUT");
    redirect_case(c, &r, 302, sha);
    CHECK(!failure[0]);
    r.method = text("POST");
    size_t n = 70000;
    uint8_t *data = pattern(n);
    temp_path(path, sizeof(path), "redirect.bin");
    CHECK_EQ(write_file(path, data, n), 0);
    sha256_hex(data, n, sha);
    r.body_kind = UGO_BODY_FILE;
    r.body = text(path);
    redirect_case(c, &r, 307, sha);
    CHECK(!failure[0]);
    ugo_mime_part parts[2];
    memset(parts, 0, sizeof(parts));
    parts[0].name = text("meta");
    parts[0].content_type = text("application/json");
    parts[0].data = text(json);
    parts[0].source = UGO_PART_MEMORY;
    parts[1].name = text("file");
    parts[1].filename = text("data.bin");
    parts[1].content_type = text("application/octet-stream");
    parts[1].data = text(path);
    parts[1].source = UGO_PART_FILE;
    r.body_kind = UGO_BODY_MULTIPART;
    r.body = text(NULL);
    r.parts = (uint64_t)(uintptr_t)parts;
    r.part_count = 2;
    redirect_case(c, &r, 308, NULL);
    free(data);
    unlink(path);
    CHECK(!failure[0]);

    r = get_request("");
    r.max_redirects = 0;
    char url[512];
    r.url = text(make_url(url, sizeof(url), http_base, "/redirect/302?to=/ok"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.h.http_status, 302);
    CHECK_EQ(t.done.redirect_count, 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r.max_redirects = 1;
    r.url = text(make_url(url, sizeof(url), http_base, "/redirect/302?to=%2Fredirect%2F302%3Fto%3D%2Fok"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.headers, 0);
    ugo_client_destroy(c);
}

static void expect_method(const char *target, const char *method);
static int recorded_has(const char *target, const char *field);

static long long recorded_port(const char *target)
{
    char *line = recorded(target);
    const char *p = line ? strstr(line, "\"peer_port\": ") : NULL;
    long long port = p ? strtoll(p + 13, NULL, 10) : -1;
    free(line);
    return port;
}

static void expect_see_other_hop(const char *target, const char *method, bool credentials)
{
    char sha[65], empty[65];
    sha256_hex("", 0, empty);
    expect_method(target, method);
    if(failure[0])
        return;
    recorded_sha(target, sha);
    if(strcmp(sha, empty))
        FAIL("%s: a body after 303 (%s)", target, sha);
    if(recorded_has(target, "\"content-type:") || recorded_has(target, "\"content-length:") ||
       recorded_has(target, "\"content-encoding:") || recorded_has(target, "\"transfer-encoding:"))
        FAIL("%s: a body header after 303", target);
    if(!recorded_has(target, "\"x-keep: 1\""))
        FAIL("%s: caller header dropped after 303", target);
    if(!recorded_has(target, "\"authorization: bearer bench-token\"") != !credentials)
        FAIL("%s: Authorization %s after 303", target, credentials ? "dropped" : "kept");
}

static const char SEE_OTHER_HEADERS[] =
    "Content-Type: application/json\r\nContent-Length: 20\r\nContent-Encoding: identity\r\n"
    "Authorization: Bearer bench-token\r\nCookie: session=1\r\nX-Keep: 1\r\n";

/* POST (or PUT/HEAD) → 303 → `final` on `base`, optionally through a 302 before or after the 303. */
static void see_other_case(ugo_client *c, const char *base, const char *method, const char *chain, track *t,
                           char *final, size_t final_size)
{
    char url[1024], first[512];
    const char *json = "{\"signed\":\"payload\"}";
    int tag = next_tag();
    snprintf(final, final_size, "/ok?t=%d", tag);
    if(!strcmp(chain, "303"))
        snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d", tag);
    else if(!strcmp(chain, "303-302"))
        snprintf(first, sizeof(first), "/redirect/303?to=%%2Fredirect%%2F302%%3Fto%%3D%%252Fok%%253Ft%%253D%d", tag);
    else
        snprintf(first, sizeof(first), "/redirect/302?to=%%2Fredirect%%2F303%%3Fto%%3D%%252Fok%%253Ft%%253D%d", tag);
    ugo_request r = get_request(make_url(url, sizeof(url), base, first));
    r.method = text(method);
    /* A HEAD carries no body, so a Content-Length on it would make the bench wait for one. */
    r.headers = text(strcmp(method, "HEAD") ? SEE_OTHER_HEADERS : strstr(SEE_OTHER_HEADERS, "Content-Encoding"));
    if(strcmp(method, "HEAD")) {
        r.body_kind = UGO_BODY_MEMORY;
        r.body = text(json);
    }
    CHECK(run_one(c, &r, t));
    if(t->done.error_class != UGO_CLASS_NONE)
        FAIL("%s %s via %s: class %u status %u", method, chain, base, t->done.error_class, t->done.http_status);
    CHECK_EQ(t->done.redirect_count, strcmp(chain, "303") ? 2 : 1);
    CHECK_EQ(t->headers, 1);
    CHECK_EQ(t->h.http_status, 200);
    CHECK(t->done.redirect_us > 0 && t->done.total_us >= t->done.redirect_us);
    char sha[65], want[65];
    recorded_sha(first, sha);
    sha256_hex(strcmp(method, "HEAD") ? json : "", strcmp(method, "HEAD") ? strlen(json) : 0, want);
    if(strcmp(sha, want))
        FAIL("%s %s: first hop got %s", method, first, sha);
    expect_see_other_hop(final, strcmp(method, "HEAD") ? "GET" : "HEAD", true);
}

static void redirect_to(char *first, size_t size, int code, const char *to)
{
    size_t n = (size_t)snprintf(first, size, "/redirect/%d?to=", code);
    for(const char *p = to; *p && n + 4 < size; ++p)
        n += isalnum((unsigned char)*p) ? (size_t)snprintf(first + n, 2, "%c", *p)
                                        : (size_t)snprintf(first + n, 4, "%%%02X", (unsigned char)*p);
}

static void test_see_other(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char final[128], url[1024], first[512], path[512];
    track t;
    ugo_result res;
    const char *bases[] = {http_base, https_base};
    const char *chains[] = {"303", "303-302", "302-303"};
    for(int b = 0; b < 2; ++b) {
        for(int i = 0; i < 3; ++i) {
            see_other_case(c, bases[b], "POST", chains[i], &t, final, sizeof(final));
            CHECK(!failure[0]);
            CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
            CHECK_EQ(res.body.len, strlen(OK_BODY));
            CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, OK_BODY, res.body.len));
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        }
        see_other_case(c, bases[b], "PUT", "303", &t, final, sizeof(final));
        CHECK(!failure[0]);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        see_other_case(c, bases[b], "HEAD", "303", &t, final, sizeof(final));
        CHECK(!failure[0]);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK_EQ(res.body.len, 0);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }

    /* HTTP/2 keeps the connection for the GET; HTTP/1.1 loses it, as curl closes it after any callback abort. */
    ugo_client *fresh = client_new(1);
    CHECK(fresh);
    int tag = 0;
    ugo_request r;
    for(int k = 0; k < 2; ++k) {
        tag = next_tag();
        snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d&reuse=1", tag);
        snprintf(final, sizeof(final), "/ok?t=%d", tag);
        r = get_request(make_url(url, sizeof(url), https_base, first));
        r.method = text("POST");
        r.body_kind = UGO_BODY_MEMORY;
        r.body = text("x");
        CHECK(run_one(fresh, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK(recorded_port(first) > 0);
        CHECK_EQ(recorded_port(final), recorded_port(first));
        CHECK_EQ(t.done.connection_reused, k);
        CHECK_EQ(ugo_release(fresh, t.id), UGO_OK);
    }
    ugo_client_destroy(fresh);

    tag = next_tag();
    snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d", tag);
    r = get_request(make_url(url, sizeof(url), http_base, first));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("x");
    r.total_timeout_ms = 3000000000u;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    const char *keep = "Authorization: Bearer bench-token\r\nX-Keep: 1\r\n";
    char keep_te[128], sha[65];
    snprintf(keep_te, sizeof(keep_te), "%sTransfer-Encoding: chunked\r\n", keep);
    const char *json = "{\"signed\":\"payload\"}";
    size_t file_len = 70000;
    uint8_t *file_data = pattern(file_len);
    char file_sha[65], json_sha[65];
    sha256_hex(file_data, file_len, file_sha);
    sha256_hex(json, strlen(json), json_sha);
    temp_path(path, sizeof(path), "see-other.bin");
    CHECK_EQ(write_file(path, file_data, file_len), 0);
    free(file_data);
    char mime_path[512];
    const char *mime_file = "file part payload";
    temp_path(mime_path, sizeof(mime_path), "see-other-multipart.txt");
    CHECK_EQ(write_file(mime_path, mime_file, strlen(mime_file)), 0);
    ugo_mime_part parts[2];
    memset(parts, 0, sizeof(parts));
    parts[0].name = text("meta");
    parts[0].content_type = text("text/plain");
    parts[0].data = text("memory part payload");
    parts[0].source = UGO_PART_MEMORY;
    parts[1].name = text("file");
    parts[1].filename = text("data.bin");
    parts[1].content_type = text("application/octet-stream");
    parts[1].data = text(mime_path);
    parts[1].source = UGO_PART_FILE;
    for(int b = 0; b < 2; ++b) {
        for(int k = 0; k < 4; ++k) {
            tag = next_tag();
            snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d%s", tag, k == 2 ? "&record-body=1" : "");
            snprintf(final, sizeof(final), "/ok?t=%d", tag);
            r = get_request(make_url(url, sizeof(url), bases[b], first));
            r.method = text(k == 1 ? "PUT" : "POST");
            r.headers = text(k == 3 ? keep_te : keep);
            if(k < 2) {
                r.body_kind = UGO_BODY_FILE;
                r.body = text(path);
            } else if(k == 2) {
                r.body_kind = UGO_BODY_MULTIPART;
                r.parts = (uint64_t)(uintptr_t)parts;
                r.part_count = 2;
            } else {
                r.body_kind = UGO_BODY_MEMORY;
                r.body = text(json);
            }
            CHECK(run_one(c, &r, &t));
            if(t.done.error_class != UGO_CLASS_NONE)
                FAIL("303 body kind %d via %s: class %u curl %d", k, bases[b], t.done.error_class, t.done.curl_code);
            CHECK_EQ(t.done.redirect_count, 1);
            recorded_sha(first, sha);
            if(k == 2) {
                char *line = recorded(first);
                bool memory = line && strstr(line, "name=\\\"meta\\\"") &&
                              strstr(line, "\\r\\n\\r\\nmemory part payload\\r\\n");
                bool file = line && strstr(line, "name=\\\"file\\\"") &&
                            strstr(line, "\\r\\n\\r\\nfile part payload\\r\\n");
                free(line);
                CHECK(memory && file);
                CHECK(recorded_has(first, "multipart/form-data"));
            } else if(strcmp(sha, k < 2 ? file_sha : json_sha)) {
                FAIL("303 body kind %d via %s: first hop got %s", k, bases[b], sha);
            }
            if(k == 3 && !b && !recorded_has(first, "\"transfer-encoding: chunked\""))
                FAIL("303 via %s: the upload was not chunked", bases[b]);
            expect_see_other_hop(final, "GET", true);
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
            CHECK(!failure[0]);
        }
    }
    unlink(path);
    unlink(mime_path);

    char to[512], want[300];
    for(int b = 0; b < 2; ++b) {
        char cross[256];
        const char *ip = strstr(bases[b], "127.0.0.1");
        CHECK(ip);
        snprintf(cross, sizeof(cross), "%.*slocalhost%s", (int)(ip - bases[b]), bases[b], ip + 9);
        const int codes[] = {303, 303, 302};
        for(int k = 0; k < 3; ++k) {
            tag = next_tag();
            snprintf(final, sizeof(final), "/ok?t=%d", tag);
            snprintf(to, sizeof(to), "%s%s", k == 1 ? bases[b] : cross, final);
            redirect_to(first, sizeof(first), codes[k], to);
            r = get_request(make_url(url, sizeof(url), bases[b], first));
            r.method = text("POST");
            r.headers = text("Host: original.example\r\n");
            r.body_kind = UGO_BODY_MEMORY;
            r.body = text("x");
            CHECK(run_one(c, &r, &t));
            CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
            snprintf(want, sizeof(want), "\"%s: %s\"", b ? ":authority" : "host",
                     k == 1 ? "original.example" : strstr(cross, "://") + 3);
            if(!recorded_has(final, want))
                FAIL("%d via %s: final hop lacks %s", codes[k], bases[b], want);
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
            CHECK(!failure[0]);
        }
    }

    tag = next_tag();
    snprintf(final, sizeof(final), "/ok?t=%d", tag);
    snprintf(to, sizeof(to), "%s%s", https_base, final);
    redirect_to(first, sizeof(first), 303, to);
    r = get_request(make_url(url, sizeof(url), http_base, first));
    r.method = text("POST");
    r.headers = text(SEE_OTHER_HEADERS);
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("{\"signed\":\"payload\"}");
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 1);
    expect_see_other_hop(final, "GET", false);
    CHECK(!recorded_has(final, "\"cookie:"));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    tag = next_tag();
    snprintf(first, sizeof(first), "/redirect/302?to=%%2Fredirect%%2F303%%3Fto%%3D%%252Fredirect%%252F302%%253Fto%%253D%%25252Fok");
    r = get_request(make_url(url, sizeof(url), http_base, first));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("x");
    r.max_redirects = 2;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.curl_code, 47);
    CHECK_EQ(t.done.redirect_count, 2);
    CHECK_EQ(t.headers, 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r.max_redirects = 3;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 3);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r.max_redirects = 1;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.curl_code, 47);
    CHECK_EQ(t.done.redirect_count, 1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    tag = next_tag();
    snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d", tag);
    r = get_request(make_url(url, sizeof(url), http_base, first));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("x");
    r.memory_limit = 0;
    r.sink_path = text(temp_path(path, sizeof(path), "see-other.part"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    size_t len;
    char *data = read_file(path, &len);
    bool same = data && len == strlen(OK_BODY) && !memcmp(data, OK_BODY, len);
    free(data);
    unlink(path);
    CHECK(same);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    for(int b = 0; b < 2; ++b) {
        tag = next_tag();
        snprintf(first, sizeof(first), "/redirect/303?to=%%2Frange%%2F5000%%3Ft%%3D%d", tag);
        snprintf(final, sizeof(final), "/range/5000?t=%d", tag);
        temp_path(path, sizeof(path), "see-other-resume.part");
        CHECK_EQ(write_range_prefix(path, 1000, 0), 0);
        r = get_request(make_url(url, sizeof(url), bases[b], first));
        r.memory_limit = 0;
        r.sink_path = text(path);
        r.flags = UGO_REQ_RESUME;
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(t.h.http_status, 206);
        CHECK_EQ(t.h.resume_offset, 1000);
        CHECK(range_file_is(path, 5000, 0));
        CHECK(recorded_has(final, "range: bytes=1000-"));
        unlink(path);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }

    tag = next_tag();
    snprintf(first, sizeof(first), "/redirect/303?to=%%2Fok%%3Ft%%3D%d", tag);
    snprintf(final, sizeof(final), "/ok?t=%d", tag);
    r = get_request(make_url(url, sizeof(url), http_base, first));
    r.method = text("POST");
    r.headers = text(SEE_OTHER_HEADERS);
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("{\"signed\":\"payload\"}");
    r.memory_limit = 0;
    r.flags = UGO_REQ_DETACHED;
    uint64_t id;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    int64_t end = ugo_monotonic_us() + 5000000;
    while(ugo_get_result(c, id, &res) == UGO_E_STATE && ugo_monotonic_us() < end)
        sleep_ms(5);
    CHECK_EQ(ugo_get_result(c, id, &res), UGO_E_STALE_ID);
    CHECK(quiet(c, 100));
    expect_see_other_hop(final, "GET", true);

    r = get_request(make_url(url, sizeof(url), http_base, "/redirect/303?to=/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text("x");
    r.max_redirects = 0;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.h.http_status, 303);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void expect_method(const char *target, const char *method)
{
    char *line = recorded(target), want[64];
    snprintf(want, sizeof(want), "\"method\": \"%s\"", method);
    bool ok = line && strstr(line, want);
    free(line);
    if(!ok)
        FAIL("%s: not sent as %s", target, method);
}

static void expect_headers(const char *target, const char *names)
{
    char got[512];
    header_names(target, got, sizeof(got));
    if(strcmp(got, names))
        FAIL("%s: headers [%s], expected [%s]", target, got, names);
}

static void send_method(ugo_client *c, ugo_request *r, const char *method, const char *body, size_t len)
{
    char url[512], target[128], sha[65], want[65];
    snprintf(target, sizeof(target), "/ok?t=%d", next_tag());
    r->url = text(make_url(url, sizeof(url), http_base, target));
    r->method = text(method);
    track t;
    CHECK(run_one(c, r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.bytes_sent, len);
    expect_method(target, method);
    CHECK(!failure[0]);
    recorded_sha(target, sha);
    sha256_hex(body, len, want);
    if(strcmp(sha, want))
        FAIL("%s %s: recorded %s, sent %s", method, target, sha, want);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
}

static void test_methods(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], target[128], path[512];
    snprintf(target, sizeof(target), "/ok?t=%d", next_tag());
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, target));
    r.method = text("HEAD");
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 200);
    CHECK_EQ(t.h.content_length, strlen(OK_BODY));
    CHECK_EQ(t.done.bytes_received, 0);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    expect_method(target, "HEAD");
    expect_headers(target, "accept-encoding,host");
    CHECK(!failure[0]);

    const char *json = "{\"put\":true}";
    r = get_request("");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text(json);
    send_method(c, &r, "PUT", json, strlen(json));
    send_method(c, &r, "GET", json, strlen(json));
    send_method(c, &r, "post", json, strlen(json));
    CHECK(!failure[0]);

    size_t n = 50000;
    uint8_t *data = pattern(n);
    temp_path(path, sizeof(path), "patch.bin");
    CHECK_EQ(write_file(path, data, n), 0);
    r.body_kind = UGO_BODY_FILE;
    r.body = text(path);
    send_method(c, &r, "PATCH", (const char *)data, n);
    free(data);
    unlink(path);
    CHECK(!failure[0]);

    ugo_mime_part part;
    memset(&part, 0, sizeof(part));
    part.name = text("field");
    part.data = text("value");
    part.source = UGO_PART_MEMORY;
    r.body_kind = UGO_BODY_MULTIPART;
    r.body = text(NULL);
    r.parts = (uint64_t)(uintptr_t)&part;
    r.part_count = 1;
    snprintf(target, sizeof(target), "/ok?t=%d", next_tag());
    r.url = text(make_url(url, sizeof(url), http_base, target));
    r.method = text("DELETE");
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK(t.done.bytes_sent > 5);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    expect_method(target, "DELETE");
    CHECK(!failure[0]);

    r = get_request("");
    const char *bodiless[] = {"DELETE", "OPTIONS", "POST", "X-PURGE_1~", "get"};
    for(size_t i = 0; i < sizeof(bodiless) / sizeof(bodiless[0]); ++i) {
        send_method(c, &r, bodiless[i], "", 0);
        CHECK(!failure[0]);
    }
    ugo_client_destroy(c);
}

static void test_wire_headers(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], target[128];
    ugo_request r = get_request("");
    track t;
    struct {
        const char *method;
        uint32_t body_kind;
        size_t len;
        const char *headers;
        const char *names[2];
    } cases[] = {
        {"GET", UGO_BODY_NONE, 0, NULL, {"accept-encoding,host", ":authority,:method,:path,:scheme,accept-encoding"}},
        {"POST", UGO_BODY_NONE, 0, NULL, {"accept-encoding,host", ":authority,:method,:path,:scheme,accept-encoding"}},
        {"POST", UGO_BODY_MEMORY, 0, NULL, {"accept-encoding,content-length,host", ":authority,:method,:path,:scheme,accept-encoding,content-length"}},
        {"POST", UGO_BODY_MEMORY, 2u << 20, NULL, {"accept-encoding,content-length,host", ":authority,:method,:path,:scheme,accept-encoding,content-length"}},
        {"PUT", UGO_BODY_MEMORY, 2u << 20, NULL, {"accept-encoding,content-length,host", ":authority,:method,:path,:scheme,accept-encoding,content-length"}},
        {"POST", UGO_BODY_MULTIPART, 0, NULL,
         {"accept-encoding,content-length,content-type,host", ":authority,:method,:path,:scheme,accept-encoding,content-length,content-type"}},
        {"POST", UGO_BODY_MEMORY, 3, "Content-Type: text/plain\nAccept: application/json\nUser-Agent: bench/1\n",
         {"accept,accept-encoding,content-length,content-type,host,user-agent", ":authority,:method,:path,:scheme,accept,accept-encoding,content-length,content-type,user-agent"}},
        {"POST", UGO_BODY_MEMORY, 3, "Expect: 100-continue\n",
         {"accept-encoding,content-length,expect,host", ":authority,:method,:path,:scheme,accept-encoding,content-length,expect"}},
        {"PUT", UGO_BODY_MEMORY, 3, "Accept;\nExpect;\nContent-Type;\n",
         {"accept,accept-encoding,content-length,content-type,expect,host", ":authority,:method,:path,:scheme,accept,accept-encoding,content-length,content-type,expect"}},
        {"GET", UGO_BODY_NONE, 0, "Accept-Encoding: identity\n",
         {"accept-encoding,host", ":authority,:method,:path,:scheme,accept-encoding"}},
        {"GET", UGO_BODY_NONE, 0, "Accept-Encoding:\n", {"host", ":authority,:method,:path,:scheme"}},
        {"GET", UGO_BODY_NONE, 0, "X-Empty;\nX-Two: 2\nX-Two: 3\n",
         {"accept-encoding,host,x-empty,x-two,x-two", ":authority,:method,:path,:scheme,accept-encoding,x-empty,x-two,x-two"}},
    };
    uint8_t *data = pattern(2u << 20);
    ugo_mime_part part;
    memset(&part, 0, sizeof(part));
    part.name = text("field");
    part.data = text("value");
    part.source = UGO_PART_MEMORY;
    for(int protocol = 0; protocol < 2; ++protocol) {
        for(size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
            snprintf(target, sizeof(target), "/ok?t=%d", next_tag());
            r.url = text(make_url(url, sizeof(url), protocol ? https_base : http_base, target));
            r.method = text(cases[i].method);
            r.body_kind = cases[i].body_kind;
            r.body = cases[i].body_kind == UGO_BODY_MEMORY ? bytes(data, cases[i].len) : text(NULL);
            r.parts = cases[i].body_kind == UGO_BODY_MULTIPART ? (uint64_t)(uintptr_t)&part : 0;
            r.part_count = cases[i].body_kind == UGO_BODY_MULTIPART;
            r.headers = text(cases[i].headers);
            CHECK(run_one(c, &r, &t));
            CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
            CHECK_EQ(t.done.http_version, protocol ? 20 : 11);
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
            expect_headers(target, cases[i].names[protocol]);
            CHECK(!failure[0]);
        }
        char *line = recorded(target);
        CHECK(line);
        bool empty = strstr(line, protocol ? "\"x-empty: \"" : "\"X-Empty:\"") != NULL;
        free(line);
        CHECK(empty);
    }
    free(data);
    ugo_client_destroy(c);
}

static void test_error_body_file(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    temp_path(path, sizeof(path), "error.part");
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/status/500?bytes=1000"));
    r.sink_path = text(path);
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
    CHECK_EQ(t.done.http_status, 500);
    CHECK_EQ(file_size(path), -1);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 1000);
    uint8_t *want = malloc(200000);
    for(size_t i = 0; i < 200000; ++i)
        want[i] = (uint8_t)i;
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, want, 1000));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.url = text(make_url(url, sizeof(url), http_base, "/status/500?bytes=65536"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
    CHECK_EQ(t.done.curl_code, 0);
    CHECK_EQ(t.done.bytes_received, UGO_ERROR_BODY_MAX);
    CHECK_EQ(file_size(path), -1);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, UGO_ERROR_BODY_MAX);
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, want, UGO_ERROR_BODY_MAX));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.url = text(make_url(url, sizeof(url), http_base, "/status/503?bytes=200000"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    CHECK_EQ(t.done.curl_code, 23);
    CHECK(t.done.bytes_received < 200000);
    CHECK_EQ(t.done.http_status, 503);
    CHECK_EQ(file_size(path), -1);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, UGO_ERROR_BODY_MAX);
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, want, UGO_ERROR_BODY_MAX));
    CHECK(!memcmp((const char *)(uintptr_t)res.error_message.ptr, "HTTP status 503", 15));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.sink_path = text(NULL);
    r.memory_limit = 5000;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    CHECK_EQ(t.done.curl_code, 23);
    CHECK(t.done.bytes_received < 200000);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 5000);
    CHECK(!memcmp((const void *)(uintptr_t)res.body.ptr, want, 5000));
    free(want);
    ugo_client_destroy(c);
}

static bool header_is(ugo_bytes block, const char *name, const char *value)
{
    const char *p = (const char *)(uintptr_t)block.ptr, *end = p + block.len;
    size_t n = strlen(name), m = strlen(value);
    while(p < end) {
        const char *eol = memchr(p, '\n', (size_t)(end - p));
        if(!eol)
            eol = end;
        if((size_t)(eol - p) > n && !strncasecmp(p, name, n) && p[n] == ':') {
            const char *v = p + n + 1;
            while(v < eol && *v == ' ')
                ++v;
            const char *e = eol;
            while(e > v && (e[-1] == '\r' || e[-1] == ' '))
                --e;
            return (size_t)(e - v) == m && !strncasecmp(v, value, m);
        }
        p = eol + 1;
    }
    return false;
}

static bool counting(const void *data, size_t len)
{
    const uint8_t *b = data;
    for(size_t i = 0; i < len; ++i)
        if(b[i] != (uint8_t)i)
            return false;
    return true;
}

static void expect_encoded_get(ugo_client *c, const char *base, const char *kind, int protocol)
{
    char url[512], target[160];
    snprintf(target, sizeof(target), "/encoded/%s?bytes=200000&t=%d", kind, next_tag());
    ugo_request r = get_request(make_url(url, sizeof(url), base, target));
    track t;
    ugo_result res;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_version, protocol ? UGO_HTTP_2 : UGO_HTTP_1_1);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 200000);
    CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
    CHECK(header_is(res.headers, "content-encoding", kind));
    CHECK(t.h.content_length > 0 && t.h.content_length < 20000);
    CHECK_EQ(t.done.bytes_received, t.h.content_length);
    CHECK(t.progress > 0);
    CHECK(t.p.received > 0 && (int64_t)t.p.received <= t.h.content_length);
    CHECK_EQ(t.p.receive_total, t.h.content_length);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    char *line = recorded(target);
    bool sent = line && strstr(line, protocol ? "\"accept-encoding: gzip\"" : "\"Accept-Encoding: gzip\"");
    free(line);
    CHECK(sent);
}

static int recorded_has(const char *target, const char *field);

static void test_content_encoding(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], target[160], path[512], sha[65];
    track t;
    ugo_result res;
    for(int protocol = 0; protocol < 2; ++protocol) {
        const char *base = protocol ? https_base : http_base;
        expect_encoded_get(c, base, "gzip", protocol);
        expect_encoded_get(c, base, "deflate", protocol);
        CHECK(!failure[0]);

        const char *own[] = {"Accept-Encoding: gzip\n", "Accept-Encoding:\n"};
        for(int i = 0; i < 2; ++i) {
            snprintf(target, sizeof(target), "/encoded/gzip?bytes=200000&t=%d", next_tag());
            ugo_request r = get_request(make_url(url, sizeof(url), base, target));
            r.headers = text(own[i]);
            CHECK(run_one(c, &r, &t));
            CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
            CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
            CHECK_EQ((long long)res.body.len, t.h.content_length);
            sha256_hex((const void *)(uintptr_t)res.body.ptr, res.body.len, sha);
            CHECK(header_is(res.headers, "x-encoded-sha256", sha));
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
            char *line = recorded(target);
            CHECK(line);
            for(char *q = line; *q; ++q)
                *q = (char)(*q >= 'A' && *q <= 'Z' ? *q + 32 : *q);
            bool gzip_sent = strstr(line, "\"accept-encoding: gzip\"") != NULL;
            bool any_sent = strstr(line, "accept-encoding") != NULL;
            free(line);
            CHECK(i ? !any_sent : gzip_sent);
        }

        struct { const char *path; long code; } broken[] = {
            {"/encoded/corrupt?bytes=200000", 61}, {"/encoded/unsupported", 61}, {"/encoded/trailing", 23}};
        for(size_t i = 0; i < sizeof(broken) / sizeof(broken[0]); ++i) {
            ugo_request r = get_request(make_url(url, sizeof(url), base, broken[i].path));
            CHECK(run_one(c, &r, &t));
            CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
            CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
            CHECK_EQ(t.done.curl_code, broken[i].code);
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        }

        ugo_request r = get_request(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=1000000"));
        r.memory_limit = 65536;
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
        CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
        CHECK(t.h.content_length > 0 && t.h.content_length < 65536);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK_EQ(res.body.len, 65536);
        CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        temp_path(path, sizeof(path), "encoded.part");
        r = get_request(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=200000"));
        r.sink_path = text(path);
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        size_t len = 0;
        char *file = read_file(path, &len);
        bool whole = file && len == 200000 && counting(file, len);
        free(file);
        unlink(path);
        CHECK(whole);

        r.url = text(make_url(url, sizeof(url), base, "/encoded/corrupt?bytes=200000"));
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
        CHECK_EQ(file_size(path), -1);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        r.url = text(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=1000&status=500"));
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
        CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
        CHECK_EQ(file_size(path), -1);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK_EQ(res.body.len, 1000);
        CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        r.url = text(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=200000&status=503"));
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
        CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
        CHECK(t.h.content_length < UGO_ERROR_BODY_MAX);
        CHECK_EQ(file_size(path), -1);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK_EQ(res.body.len, UGO_ERROR_BODY_MAX);
        CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        r = get_request(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=200000"));
        r.method = text("HEAD");
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK(t.h.content_length > 0);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK_EQ(res.body.len, 0);
        CHECK(header_is(res.headers, "content-encoding", "gzip"));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }

    const char *resume_headers[] = {NULL, "Accept-Encoding: gzip\n"};
    for(int i = 0; i < 2; ++i) {
        temp_path(path, sizeof(path), "resume-encoded.part");
        unlink(path);
        snprintf(target, sizeof(target), "/encoded/gzip?bytes=200000&t=%d", next_tag());
        ugo_request r = get_request(make_url(url, sizeof(url), http_base, target));
        r.sink_path = text(path);
        r.flags = UGO_REQ_RESUME;
        r.headers = text(resume_headers[i]);
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        size_t len = 0;
        char *file = read_file(path, &len);
        CHECK(file);
        sha256_hex(file, len, sha);
        free(file);
        unlink(path);
        CHECK_EQ((long long)len, t.h.content_length);
        CHECK(header_is(res.headers, "x-encoded-sha256", sha));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        CHECK(recorded_has(target, i ? "accept-encoding: gzip" : "accept-encoding: identity"));
        CHECK(i || !recorded_has(target, "accept-encoding: gzip"));
    }

    ugo_request r = get_request(make_url(url, sizeof(url), http_base,
                                         "/redirect/302?to=%2Fencoded%2Fgzip%3Fbytes%3D200000"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 1);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 200000);
    CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r = get_request(make_url(url, sizeof(url), http_base, "/status/204"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_status, 204);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_content_encoding_truncated(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512];
    track t;
    ugo_result res;
    struct { const char *path; uint32_t cls; long long body; bool h1_only; } cases[] = {
        {"/encoded/gzip?bytes=200000&cut=5", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/gzip?bytes=200000&cut=500", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/gzip?bytes=200000&cut=-8", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/gzip?bytes=200000&cut=-4", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/deflate?bytes=200000&cut=200", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/deflate?bytes=200000&cut=-4", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/raw-deflate?bytes=200000&cut=200", UGO_CLASS_TRANSFER, -1, false},
        {"/encoded/gzip?bytes=200000&cut=500&chunked=1", UGO_CLASS_TRANSFER, -1, true},
        {"/encoded/raw-deflate?bytes=200000", UGO_CLASS_NONE, 200000, false},
        {"/encoded/gzip?bytes=200000&chunked=1", UGO_CLASS_NONE, 200000, true},
        {"/encoded/empty", UGO_CLASS_NONE, 0, false},
        {"/encoded/empty-stream", UGO_CLASS_NONE, 0, false},
        {"/redirect/302?gzip-body=1&to=%2Fencoded%2Fgzip%3Fbytes%3D200000", UGO_CLASS_NONE, 200000, true},
        {"/encoded/gzip?bytes=200000&cut=5&status=204", UGO_CLASS_NONE, 0, true},
        {"/encoded/gzip?bytes=200000&cut=5&status=304", UGO_CLASS_HTTP_STATUS, 0, true},
    };
    for(int protocol = 0; protocol < 2; ++protocol) {
        const char *base = protocol ? https_base : http_base;
        for(size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
            if(protocol && cases[i].h1_only)
                continue;
            ugo_request r = get_request(make_url(url, sizeof(url), base, cases[i].path));
            CHECK(run_one(c, &r, &t));
            if(t.done.error_class != cases[i].cls)
                FAIL("%s (h%d): class %u, expected %u", cases[i].path, protocol ? 2 : 1,
                     t.done.error_class, cases[i].cls);
            CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
            CHECK_EQ(t.done.curl_code, cases[i].cls == UGO_CLASS_TRANSFER ? 61 : 0);
            CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
            if(cases[i].body >= 0)
                CHECK_EQ(res.body.len, cases[i].body);
            CHECK(counting((const void *)(uintptr_t)res.body.ptr, res.body.len));
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        }

        ugo_request r = get_request(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=200000&cut=5"));
        r.method = text("HEAD");
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        temp_path(path, sizeof(path), "truncated.part");
        r = get_request(make_url(url, sizeof(url), base, "/encoded/gzip?bytes=200000&cut=500"));
        r.sink_path = text(path);
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
        CHECK_EQ(t.done.curl_code, 61);
        CHECK_EQ(file_size(path), -1);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);

        r.flags = UGO_REQ_KEEP_PARTIAL;
        CHECK(run_one(c, &r, &t));
        CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        size_t len = 0;
        char *file = read_file(path, &len);
        bool prefix = file && len > 0 && len < 200000 && counting(file, len);
        free(file);
        unlink(path);
        CHECK(prefix);
    }
    ugo_client_destroy(c);
}

static void test_speed_limits(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    size_t n = 100000;
    uint8_t *data = pattern(n);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = bytes(data, n);
    r.max_send_bytes_per_s = 50000;
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.bytes_sent, n);
    if(t.done.total_us < 1000000)
        FAIL("100000 bytes at 50000 B/s took %lld us", (long long)t.done.total_us);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r = get_request(make_url(url, sizeof(url), http_base, "/slow-body/100000?bps=500"));
    r.low_speed_bytes_per_s = 4000;
    r.low_speed_time_s = 1;
    int64_t start = ugo_monotonic_us();
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TIMEOUT);
    CHECK(ugo_monotonic_us() - start < 10000000);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.low_speed_bytes_per_s = 100;
    r.url = text(make_url(url, sizeof(url), http_base, "/slow-body/3000?bps=2000"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    free(data);
    ugo_client_destroy(c);
}

static void test_invalid_requests(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    uint64_t id = 7;
    const char *bad[] = {"notaurl", "ftp://127.0.0.1/", "ws://127.0.0.1/", "wss://127.0.0.1/",
                         "file:///etc/passwd", "http://", "http://exa mple.com/", "//127.0.0.1/ok", "/ok"};
    for(size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i) {
        ugo_request r = get_request(bad[i]);
        if(ugo_submit(c, &r, &id) != UGO_E_INVALID_ARG)
            FAIL("url accepted: %s", bad[i]);
    }
    char url[512];
    make_url(url, sizeof(url), http_base, "/ok");
    ugo_request base = get_request(url);
    ugo_request r = base;
    r.url = bytes("http://127.0.0.1/\0x", 19);
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.url = text(NULL);
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.flags = 1u << 7;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    const char *methods[] = {"", "CONNECT", "GE T", "GET\r\n", "(GET)", "G\xc3\xa9T", "GET/1", "GET\"",
                             "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456"};
    for(size_t i = 0; i < sizeof(methods) / sizeof(methods[0]); ++i) {
        r = base;
        r.method = text(methods[i]);
        if(ugo_submit(c, &r, &id) != UGO_E_INVALID_ARG)
            FAIL("method accepted: %s", methods[i]);
    }
    r = base; r.method = bytes("GET\0", 4);
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.method = bytes(NULL, 3);
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.method = text("HEAD"); r.body_kind = UGO_BODY_MEMORY; r.body = text("x");
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.low_speed_bytes_per_s = 100;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.low_speed_time_s = 1;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.low_speed_bytes_per_s = 100; r.low_speed_time_s = 65536;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.memory_limit = 0;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.connect_timeout_ms = 0;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.flags = UGO_REQ_DETACHED; r.sink_path = text("/tmp/x");
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.method = text("POST"); r.body_kind = UGO_BODY_MULTIPART;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.method = text("POST"); r.body_kind = 9;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r = base; r.headers.len = 3;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_submit(c, NULL, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_submit(NULL, &base, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(id, 7);
    CHECK(quiet(c, 50));
    r = base;
    r.low_speed_bytes_per_s = 100;
    r.low_speed_time_s = 65535;
    r.flags = UGO_REQ_FINISH_IN_BACKGROUND;
    r.headers = text("X-One: 1\r\nX-Two: 2\n\nAccept:\r\n");
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    __atomic_store_n(&ugo_test_bg_expire_early, true, __ATOMIC_RELAXED);
    int ran = run_one(c, &r, &t);
    __atomic_store_n(&ugo_test_bg_expire_early, false, __ATOMIC_RELAXED);
    CHECK(ran);
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.done.detail, UGO_DETAIL_BACKGROUND_EXPIRED);
    ugo_client_destroy(c);
}

static void test_abort(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    track t;
    memset(&t, 0, sizeof(t));
    CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(drain(c, &t, 1, 5000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.done.detail, UGO_DETAIL_CALLER);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(quiet(c, 200));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    memset(&t, 0, sizeof(t));
    CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
    int64_t end = ugo_monotonic_us() + 5000000;
    while(!t.headers && ugo_monotonic_us() < end) {
        ugo_event e[4];
        int32_t k = ugo_wait_events(c, e, 4, 100);
        for(int i = 0; i < k; ++i)
            apply(&t, 1, &e[i]);
    }
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.completed, 0);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(drain(c, &t, 1, 5000));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.done.detail, UGO_DETAIL_CALLER);
    CHECK(quiet(c, 200));
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(res.headers.len > 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    int aborted = 0, finished = 0;
    for(int i = 0; i < 60; ++i) {
        memset(&t, 0, sizeof(t));
        CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
        int64_t spin = ugo_monotonic_us() + (i % 20) * 150;
        while(ugo_monotonic_us() < spin)
            ;
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK(drain(c, &t, 1, 5000));
        CHECK_EQ(t.completed, 1);
        CHECK_EQ(t.late, 0);
        if(t.done.error_class == UGO_CLASS_ABORTED) {
            aborted++;
        } else {
            CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
            CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
            CHECK_EQ(res.body.len, strlen(OK_BODY));
            finished++;
        }
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    CHECK(quiet(c, 100));
    CHECK(aborted > 0);
    ugo_client_destroy(c);
    (void)finished;
}

static void test_release_state(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t id;
    ugo_result res;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    CHECK_EQ(ugo_release(c, id), UGO_E_STATE);
    CHECK_EQ(ugo_get_result(c, id, &res), UGO_E_STATE);
    CHECK_EQ(ugo_abort(c, id), UGO_OK);
    sleep_ms(300);
    CHECK_EQ(ugo_release(c, id), UGO_E_STATE);
    CHECK_EQ(ugo_get_result(c, id, &res), UGO_E_STATE);
    track t = {0};
    t.id = id;
    CHECK(drain(c, &t, 1, 5000));
    CHECK_EQ(ugo_get_result(c, id, &res), UGO_OK);
    CHECK_EQ(ugo_release(c, id), UGO_OK);
    CHECK_EQ(ugo_release(c, id), UGO_E_STALE_ID);
    ugo_client_destroy(c);
}

static void test_capacity(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    track t[2];
    memset(t, 0, sizeof(t));
    uint64_t extra = 0;
    CHECK_EQ(ugo_submit(c, &r, &t[0].id), UGO_OK);
    CHECK_EQ(ugo_submit(c, &r, &t[1].id), UGO_OK);
    CHECK_EQ(ugo_submit(c, &r, &extra), UGO_E_CAPACITY);
    r.flags = UGO_REQ_DETACHED;
    CHECK_EQ(ugo_submit(c, &r, &extra), UGO_E_CAPACITY);
    CHECK_EQ(extra, 0);
    CHECK((uint32_t)t[0].id < 2 && (uint32_t)t[1].id < 2);
    CHECK_EQ(ugo_abort(c, t[0].id), UGO_OK);
    CHECK_EQ(ugo_abort(c, t[1].id), UGO_OK);
    CHECK(drain(c, t, 2, 5000));
    CHECK_EQ(ugo_submit(c, &r, &extra), UGO_E_CAPACITY);
    CHECK_EQ(ugo_release(c, t[0].id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_detached(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], target[64], sha[65], want[65];
    char body[] = "telemetry-payload-0123456789";
    sha256_hex(body, strlen(body), want);
    snprintf(target, sizeof(target), "/ok?t=%d", next_tag());
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, target));
    r.method = text("POST");
    r.body_kind = UGO_BODY_MEMORY;
    r.body = text(body);
    r.flags = UGO_REQ_DETACHED;
    r.memory_limit = 0;
    uint64_t id;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    memset(body, 'X', strlen(body));
    ugo_result res;
    int32_t state = ugo_get_result(c, id, &res);
    CHECK(state == UGO_E_STATE || state == UGO_E_STALE_ID);
    int64_t end = ugo_monotonic_us() + 5000000;
    while(ugo_get_result(c, id, &res) == UGO_E_STATE && ugo_monotonic_us() < end)
        sleep_ms(5);
    CHECK_EQ(ugo_get_result(c, id, &res), UGO_E_STALE_ID);
    CHECK_EQ(ugo_abort(c, id), UGO_E_STALE_ID);
    CHECK_EQ(ugo_release(c, id), UGO_E_STALE_ID);
    CHECK(quiet(c, 200));
    recorded_sha(target, sha);
    if(strcmp(sha, want))
        FAIL("detached %s: recorded '%s', sent %s", target, sha, want);

    r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    r.flags = UGO_REQ_DETACHED;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    sleep_ms(100);
    CHECK_EQ(ugo_release(c, id), UGO_E_STATE);
    CHECK_EQ(ugo_abort(c, id), UGO_OK);
    end = ugo_monotonic_us() + 5000000;
    while(ugo_abort(c, id) == UGO_OK && ugo_monotonic_us() < end)
        sleep_ms(5);
    CHECK_EQ(ugo_abort(c, id), UGO_E_STALE_ID);
    CHECK(quiet(c, 100));
    uint64_t ids[4];
    for(int i = 0; i < 4; ++i)
        CHECK_EQ(ugo_submit(c, &r, &ids[i]), UGO_OK);
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_CAPACITY);
    ugo_client_destroy(c);
}

static void test_errors(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    r.total_timeout_ms = 300;
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TIMEOUT);
    CHECK_EQ(t.done.curl_code, 28);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request("http://127.0.0.1:1/");
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_CONNECT);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/wrong-content-length"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    ugo_client_destroy(c);
}

static void test_dns(void)
{
    if(!getenv("UGO_TEST_DNS")) {
        skipped = "UGO_TEST_DNS unset (needs a host with no DNS)";
        return;
    }
    ugo_client *c = client_new(2);
    CHECK(c);
    ugo_request r = get_request("http://does-not-exist.invalid/");
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_DNS);
    ugo_client_destroy(c);
}

static int fault(const char *args)
{
    char command[256];
    snprintf(command, sizeof(command), "fault %s 1>&2", args);
    return system(command);
}

#define FAULT_NAME "ugo-fault.test"

static void network_faults(ugo_client *c)
{
    const char *port = strrchr(http_base, ':') + 1;
    char url[512], named[512], args[128];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    r.connect_timeout_ms = 300;
    r.total_timeout_ms = 2000;
    track t;

    snprintf(args, sizeof(args), "blackhole %s", port);
    CHECK_EQ(fault(args), 0);
    int64_t start = ugo_monotonic_us();
    int done = run_one(c, &r, &t);
    int64_t took = ugo_monotonic_us() - start;
    CHECK_EQ(fault("online"), 0);
    CHECK(done);
    CHECK_EQ(t.done.error_class, UGO_CLASS_TIMEOUT);
    CHECK_EQ(t.done.curl_code, 28);
    CHECK(took < 1000000);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    CHECK_EQ(fault("offline"), 0);
    done = run_one(c, &r, &t);
    CHECK_EQ(fault("online"), 0);
    CHECK(done);
    CHECK(t.done.error_class == UGO_CLASS_TIMEOUT || t.done.error_class == UGO_CLASS_CONNECT);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    const char *modes[] = {"servfail", "nxdomain"};
    for(int i = 0; i < 2; ++i) {
        snprintf(named, sizeof(named), "http://%s-" FAULT_NAME ":%s/ok", modes[i], port);
        r.url = text(named);
        snprintf(args, sizeof(args), "dns %s %s-" FAULT_NAME, modes[i], modes[i]);
        CHECK_EQ(fault(args), 0);
        CHECK(run_one(c, &r, &t));
        if(t.done.error_class != UGO_CLASS_DNS)
            FAIL("%s: class %u", modes[i], t.done.error_class);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        snprintf(args, sizeof(args), "dns ok %s-" FAULT_NAME, modes[i]);
        CHECK_EQ(fault(args), 0);
        CHECK(run_one(c, &r, &t));
        if(t.done.error_class != UGO_CLASS_NONE)
            FAIL("after %s: class %u", modes[i], t.done.error_class);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
}

static void test_network_faults(void)
{
    if(!getenv("UGO_TEST_FAULT")) {
        skipped = "UGO_TEST_FAULT unset (needs the container fault helper)";
        return;
    }
    ugo_client_config config = {0};
    config.abi_version = UGO_ABI_VERSION;
    config.max_transfers = 2;
    config.dns_cache_ttl_s = 0;
    ugo_client *c = NULL;
    CHECK_EQ(ugo_client_create(&config, &c), UGO_OK);
    network_faults(c);
    ugo_client_destroy(c);
    if(fault("online") || fault("dns ok " FAULT_NAME))
        FAIL("fault state not restored");
}

static void test_no_consumer(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/big/5000000"));
    r.sink_path = text(temp_path(path, sizeof(path), "big.part"));
    track t = {0};
    CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
    int64_t end = ugo_monotonic_us() + 30000000;
    while(file_size(path) < 5000000 && ugo_monotonic_us() < end)
        sleep_ms(20);
    CHECK_EQ(file_size(path), 5000000);
    sleep_ms(200);
    ugo_event e[8];
    int32_t n = ugo_wait_events(c, e, 8, 0);
    CHECK_EQ(n, 3);
    for(int i = 0; i < n; ++i)
        apply(&t, 1, &e[i]);
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.progress, 1);
    CHECK_EQ(t.completed, 1);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(e[1].u.progress.received, 5000000);
    CHECK(e[0].time_us <= e[1].time_us && e[1].time_us <= e[2].time_us);
    ugo_client_destroy(c);
    unlink(path);
}

static void test_progress_partial_drain(void)
{
    static const struct { const char *path; uint64_t size; } cases[] = {
        {"/slow-body/40000?bps=80000", 40000},
        {"/big/5000000", 5000000},
    };
    ugo_client *c = client_new(2);
    CHECK(c);
    for(size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
        char url[512], path[512];
        ugo_request r = get_request(make_url(url, sizeof(url), http_base, cases[i].path));
        r.sink_path = text(temp_path(path, sizeof(path), "progress.part"));
        track t = {0};
        int64_t start = ugo_monotonic_us();
        CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
        int64_t end = start + 10000000;
        uint64_t last = 0;
        while(!t.completed && ugo_monotonic_us() < end) {
            ugo_event e;
            int32_t k = ugo_wait_events(c, &e, 1, 100);
            if(k == 1) {
                if(e.kind == UGO_EV_PROGRESS) {
                    if(e.u.progress.received <= last && t.progress)
                        FAIL("%s: progress did not change: %llu", cases[i].path, (unsigned long long)e.u.progress.received);
                    last = e.u.progress.received;
                }
                apply(&t, 1, &e);
            }
        }
        CHECK_EQ(t.completed, 1);
        CHECK_EQ(t.late, 0);
        CHECK(t.progress >= 1);
        if(i == 0)
            CHECK(t.progress >= 2);
        if(t.progress > (ugo_monotonic_us() - start) / 10000 + 2)
            FAIL("%s: %d progress events", cases[i].path, t.progress);
        CHECK_EQ(last, cases[i].size);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        unlink(path);
    }
    ugo_client_destroy(c);
}

struct waiter {
    ugo_client *c;
    int32_t result;
    int events;
    int64_t returned_us;
};

static void *wait_thread(void *arg)
{
    struct waiter *w = arg;
    ugo_event e[16];
    for(;;) {
        int32_t k = ugo_wait_events(w->c, e, 16, 30000);
        if(k <= 0) {
            w->result = k;
            break;
        }
        w->events += k;
    }
    w->returned_us = ugo_monotonic_us();
    return NULL;
}

static void test_shutdown_inflight(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t ids[3];
    for(int i = 0; i < 3; ++i)
        CHECK_EQ(ugo_submit(c, &r, &ids[i]), UGO_OK);
    sleep_ms(200);
    struct waiter w = {c, 1, 0, 0};
    pthread_t thread;
    pthread_create(&thread, NULL, wait_thread, &w);
    sleep_ms(100);
    int64_t start = ugo_monotonic_us();
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    pthread_join(thread, NULL);
    CHECK_EQ(w.result, UGO_E_SHUTDOWN);
    CHECK(w.returned_us - start < 5000000);
    CHECK(w.events >= 3);
    uint64_t id;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_SHUTDOWN);
    ugo_event e;
    CHECK_EQ(ugo_wait_events(c, &e, 1, 1000), UGO_E_SHUTDOWN);
    ugo_result res;
    for(int i = 0; i < 3; ++i) {
        CHECK_EQ(ugo_abort(c, ids[i]), UGO_OK);
        CHECK_EQ(ugo_get_result(c, ids[i], &res), UGO_OK);
        CHECK(res.headers.len > 0);
        CHECK_EQ(ugo_release(c, ids[i]), UGO_OK);
    }
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    ugo_client_destroy(c);

    c = client_new(4);
    CHECK(c);
    track t[2];
    memset(t, 0, sizeof(t));
    CHECK_EQ(ugo_submit(c, &r, &t[0].id), UGO_OK);
    CHECK_EQ(ugo_submit(c, &r, &t[1].id), UGO_OK);
    CHECK_EQ(ugo_abort(c, t[1].id), UGO_OK);
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    CHECK(drain(c, t, 2, 1000));
    CHECK_EQ(t[0].done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t[0].done.detail, UGO_DETAIL_SHUTDOWN);
    CHECK_EQ(t[1].done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t[1].done.detail, UGO_DETAIL_CALLER);
    ugo_client_destroy(c);

    c = client_new(1);
    CHECK(c);
    w.c = c;
    w.result = 1;
    pthread_create(&thread, NULL, wait_thread, &w);
    sleep_ms(100);
    start = ugo_monotonic_us();
    CHECK_EQ(ugo_wait_events(c, &e, 1, 0), UGO_E_BUSY);
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    pthread_join(thread, NULL);
    CHECK_EQ(w.result, UGO_E_SHUTDOWN);
    CHECK(w.returned_us - start < 2000000);
    ugo_client_destroy(c);
}

struct shutdown_racer {
    ugo_client *c;
    int32_t result;
};

static void *shutdown_thread(void *arg)
{
    struct shutdown_racer *s = arg;
    s->result = ugo_client_shutdown(s->c);
    return NULL;
}

static void test_concurrent_shutdown(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t id;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    pthread_t threads[4];
    struct shutdown_racer s[4];
    for(int i = 0; i < 4; ++i) {
        s[i].c = c;
        s[i].result = 1;
        pthread_create(&threads[i], NULL, shutdown_thread, &s[i]);
    }
    for(int i = 0; i < 4; ++i) {
        pthread_join(threads[i], NULL);
        CHECK_EQ(s[i].result, UGO_OK);
    }
    ugo_client_destroy(c);
}

static void test_destroy(void)
{
    ugo_client_destroy(NULL);
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t id;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    r = get_request(make_url(url, sizeof(url), http_base, "/ok"));
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_OK);
    sleep_ms(200);
    ugo_client_destroy(c);
    ugo_client_destroy(c);
    ugo_client_destroy((ugo_client *)&r);
}

static void test_shutdown_all(void)
{
    ugo_shutdown_all();
    ugo_client *a = client_new(2), *b = client_new(2), *c = client_new(2);
    CHECK(a && b && c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t id;
    CHECK_EQ(ugo_submit(b, &r, &id), UGO_OK);
    ugo_client_shutdown(c);
    CHECK_EQ(ugo_shutdown_all(), 3);
    ugo_client_destroy(a);
    ugo_client_destroy(b);
    CHECK_EQ(ugo_shutdown_all(), 0);
}

#define CONCURRENT_THREADS 4
#define CONCURRENT_PER_THREAD 60
#define CONCURRENT_IDS 4096

struct shared {
    ugo_client *c;
    pthread_mutex_t lock;
    uint64_t ids[CONCURRENT_IDS];
    int completed[CONCURRENT_IDS];
    int extra[CONCURRENT_IDS];
    int count;
    int submitted;
    int done;
    int busy;
    int stray;
    int failed_classes;
    int stop;
    const char *url;
};

static int find_id(struct shared *s, uint64_t id)
{
    for(int i = 0; i < s->count; ++i)
        if(s->ids[i] == id)
            return i;
    return -1;
}

static void *submitter(void *arg)
{
    struct shared *s = arg;
    ugo_request r = get_request(s->url);
    for(int i = 0; i < CONCURRENT_PER_THREAD; ++i) {
        uint64_t id;
        int32_t rc;
        while((rc = ugo_submit(s->c, &r, &id)) == UGO_E_CAPACITY)
            sleep_ms(1);
        if(rc != UGO_OK) {
            pthread_mutex_lock(&s->lock);
            s->failed_classes++;
            pthread_mutex_unlock(&s->lock);
            continue;
        }
        pthread_mutex_lock(&s->lock);
        s->ids[s->count++] = id;
        s->submitted++;
        pthread_mutex_unlock(&s->lock);
        if(i % 3 == 0)
            ugo_abort(s->c, id);
    }
    return NULL;
}

static void *drainer(void *arg)
{
    struct shared *s = arg;
    ugo_event e[5];
    for(;;) {
        pthread_mutex_lock(&s->lock);
        int stop = s->stop;
        pthread_mutex_unlock(&s->lock);
        if(stop)
            break;
        int32_t k = ugo_wait_events(s->c, e, 5, 50);
        if(k == UGO_E_BUSY) {
            pthread_mutex_lock(&s->lock);
            s->busy++;
            pthread_mutex_unlock(&s->lock);
            sleep_ms(1);
            continue;
        }
        for(int i = 0; i < k; ++i) {
            if(e[i].kind != UGO_EV_COMPLETED)
                continue;
            pthread_mutex_lock(&s->lock);
            int at = find_id(s, e[i].id);
            pthread_mutex_unlock(&s->lock);
            if(at < 0) {
                /* the submitter may not have recorded its id yet */
                for(int spin = 0; spin < 1000 && at < 0; ++spin) {
                    sleep_ms(1);
                    pthread_mutex_lock(&s->lock);
                    at = find_id(s, e[i].id);
                    pthread_mutex_unlock(&s->lock);
                }
            }
            uint32_t cls = e[i].u.completed.error_class;
            pthread_mutex_lock(&s->lock);
            if(at < 0)
                s->stray++;
            else if(s->completed[at]++)
                s->extra[at]++;
            else
                s->done++;
            if(cls != UGO_CLASS_NONE && cls != UGO_CLASS_ABORTED)
                s->failed_classes++;
            pthread_mutex_unlock(&s->lock);
            ugo_abort(s->c, e[i].id);
            ugo_result res;
            if(ugo_get_result(s->c, e[i].id, &res) != UGO_OK ||
               (cls == UGO_CLASS_NONE && res.body.len != strlen(OK_BODY)) ||
               ugo_release(s->c, e[i].id) != UGO_OK) {
                pthread_mutex_lock(&s->lock);
                s->failed_classes++;
                pthread_mutex_unlock(&s->lock);
            }
        }
    }
    return NULL;
}

static void test_concurrent(void)
{
    static struct shared s;
    memset(&s, 0, sizeof(s));
    pthread_mutex_init(&s.lock, NULL);
    s.c = client_new(16);
    CHECK(s.c);
    char url[512];
    s.url = make_url(url, sizeof(url), http_base, "/ok");
    pthread_t subs[CONCURRENT_THREADS], drains[2];
    for(int i = 0; i < 2; ++i)
        pthread_create(&drains[i], NULL, drainer, &s);
    for(int i = 0; i < CONCURRENT_THREADS; ++i)
        pthread_create(&subs[i], NULL, submitter, &s);
    for(int i = 0; i < CONCURRENT_THREADS; ++i)
        pthread_join(subs[i], NULL);
    int64_t end = ugo_monotonic_us() + 60000000;
    for(;;) {
        pthread_mutex_lock(&s.lock);
        int finished = s.done == s.submitted;
        pthread_mutex_unlock(&s.lock);
        if(finished || ugo_monotonic_us() > end)
            break;
        sleep_ms(10);
    }
    sleep_ms(100);
    pthread_mutex_lock(&s.lock);
    s.stop = 1;
    pthread_mutex_unlock(&s.lock);
    for(int i = 0; i < 2; ++i)
        pthread_join(drains[i], NULL);
    ugo_client_destroy(s.c);
    int extra = 0;
    for(int i = 0; i < s.count; ++i)
        extra += s.extra[i];
    CHECK_EQ(s.submitted, CONCURRENT_THREADS * CONCURRENT_PER_THREAD);
    CHECK_EQ(s.done, s.submitted);
    CHECK_EQ(extra, 0);
    CHECK_EQ(s.stray, 0);
    CHECK_EQ(s.failed_classes, 0);
    pthread_mutex_destroy(&s.lock);
}

static void test_generation_wrap(void)
{
    ugo_client *c = client_new(1);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/stall-after-headers"));
    uint64_t ids[3];
    for(int i = 0; i < 3; ++i) {
        track t = {0};
        CHECK_EQ(ugo_submit(c, &r, &t.id), UGO_OK);
        CHECK_EQ(t.id >> 32, (uint64_t)i + 1);
        CHECK_EQ((uint32_t)t.id, 0);
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK(drain(c, &t, 1, 5000));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        ids[i] = t.id;
    }
    uint64_t id = 0;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_CAPACITY);
    for(int i = 0; i < 3; ++i) {
        CHECK_EQ(ugo_abort(c, ids[i]), UGO_E_STALE_ID);
        CHECK_EQ(ugo_release(c, ids[i]), UGO_E_STALE_ID);
    }
    ugo_client_destroy(c);
}

static const char WS_PATTERN[] = "abcdefghijklmnopqrstuvwxyz0123456789";
static const char *https1_base;
static const char *tls12_base;

static char *ws_url(char *buffer, size_t size, const char *base, const char *path)
{
    const char *rest = strstr(base, "://");
    snprintf(buffer, size, "%s%s/ws/%s", !strncmp(base, "https", 5) ? "wss" : "ws", rest, path);
    return buffer;
}

static ugo_ws_request ws_request(const char *url)
{
    ugo_ws_request r;
    memset(&r, 0, sizeof(r));
    r.url = text(url);
    r.headers = text("Authorization: Bearer bench-token");
    r.connect_timeout_ms = 5000;
    r.open_timeout_ms = 10000;
    r.close_timeout_ms = 3000;
    r.max_message_bytes = 1 << 20;
    r.max_backlog_bytes = 4 << 20;
    r.max_backlog_count = 64;
    r.max_send_bytes = 1 << 20;
    return r;
}

static void test_header_leading_whitespace(void)
{
    ugo_client *c = client_new(1);
    CHECK(c);
    char http_url[512], ws_url_text[512];
    ugo_request http = get_request(make_url(http_url, sizeof(http_url), http_base, "/ok"));
    ugo_ws_request ws = ws_request(ws_url(ws_url_text, sizeof(ws_url_text), http_base, "echo"));
    const char *headers[] = {
        " Authorization: Bearer secret",
        "\tCookie: secret=abc",
        "Cookie: public=1;\r\n secret=token:abc",
        "Cookie: public=1;\r\n\tsecret=token:abc",
        "X-One: 1\n Authorization: Bearer secret\n",
        "X-One: 1\n\tCookie: secret=abc\n",
    };
    for(size_t i = 0; i < sizeof(headers) / sizeof(headers[0]); ++i) {
        uint64_t id = 7;
        http.headers = ws.headers = text(headers[i]);
        CHECK_EQ(ugo_submit(c, &http, &id), UGO_E_INVALID_ARG);
        CHECK_EQ(id, 7);
        CHECK_EQ(ugo_ws_open(c, &ws, &id), UGO_E_INVALID_ARG);
        CHECK_EQ(id, 7);
    }
    CHECK(quiet(c, 50));
    ugo_client_destroy(c);
}

static void test_header_lines_validated(void)
{
    ugo_client *c = client_new(1);
    CHECK(c);
    char http_url[512], ws_url_text[512];
    ugo_request http = get_request(make_url(http_url, sizeof(http_url), http_base, "/ok"));
    ugo_ws_request ws = ws_request(ws_url(ws_url_text, sizeof(ws_url_text), http_base, "echo"));
    static const char nul[] = "X-One: a\0b\r\n";
    ugo_bytes headers[] = {
        text("X-One: a\rb\r\n"),
        text("X-One: a\r"),
        text("X-One: 1\r\rX-Two: 2\r\n"),
        text("X-One: 1\r\r\n"),
        bytes(nul, sizeof(nul) - 1),
        bytes("X-One: 1\n\0", 10),
    };
    for(size_t i = 0; i < sizeof(headers) / sizeof(headers[0]); ++i) {
        uint64_t id = 7;
        http.headers = ws.headers = headers[i];
        if(ugo_submit(c, &http, &id) != UGO_E_INVALID_ARG || id != 7)
            FAIL("HTTP header block %zu accepted", i);
        if(ugo_ws_open(c, &ws, &id) != UGO_E_INVALID_ARG || id != 7)
            FAIL("WebSocket header block %zu accepted", i);
    }
    uint64_t id = 7;
    ws.headers = text("Authorization: Bearer bench-token\r\nSec-WebSocket-Extensions: permessage-deflate\r\n");
    CHECK_EQ(ugo_ws_open(c, &ws, &id), UGO_E_INVALID_ARG);
    ws.headers = text("sec-websocket-extensions;");
    CHECK_EQ(ugo_ws_open(c, &ws, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(id, 7);
    track t;
    http.headers = text("X-One: 1\r\nX-Two: 2\nX-Three: 3");
    CHECK(run_one(c, &http, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    CHECK(quiet(c, 50));
    ugo_client_destroy(c);
}

static void test_ws_origin_zone(void)
{
    const struct {
        const char *a;
        const char *b;
        bool same;
    } cases[] = {
        {"ws://[fe80::1]/", "ws://[fe80::1]:80/path", true},
        {"ws://[fe80::1%25en0]/", "ws://[fe80::1%25en0]:80/path", true},
        {"ws://[fe80::1%25en0]/", "ws://[fe80::1%25en1]/", false},
        {"ws://[fe80::1%251]/", "ws://[fe80::1%252]/", false},
        {"ws://[fe80::1%25en0]/", "ws://[fe80::1]/", false},
        {"ws://[fe80::1]/", "ws://[fe80::1%25en0]/", false},
        {"ws://[fe80::1%25en0]/", "ws://[fe80::1%25EN0]/", false},
        {"ws://LOCALHOST/", "ws://localhost:80/", true},
    };
    for(size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i)
        if(ugo_test_same_origin(cases[i].a, cases[i].b) != cases[i].same)
            FAIL("origin comparison: %s -> %s", cases[i].a, cases[i].b);
}

typedef struct ws_track {
    uint64_t id;
    int headers;
    int completed;
    int late;
    int messages;
    int bad;
    bool release;
    uint64_t seq;
    uint64_t bytes;
    ugo_ev_headers h;
    ugo_ev_completed done;
    size_t last_len;
    uint32_t last_type;
    int binaries;
    char last[128];
    void (*check)(struct ws_track *t, const uint8_t *data, size_t len);
} ws_track;

static void ws_apply(ugo_client *c, ws_track *t, const ugo_event *e)
{
    if(e->id != t->id) {
        t->late++;
        return;
    }
    if(t->completed)
        t->late++;
    if(e->kind == UGO_EV_HEADERS) {
        if(t->headers++ || t->messages)
            t->late++;
        t->h = e->u.headers;
    } else if(e->kind == UGO_EV_WS_MESSAGE) {
        const ugo_ev_ws_message *m = &e->u.ws_message;
        if(m->seq != t->seq + 1 || !t->headers || (m->data.len && !m->data.ptr))
            t->late++;
        t->seq = m->seq;
        t->messages++;
        t->bytes += m->data.len;
        const uint8_t *data = (const uint8_t *)(uintptr_t)m->data.ptr;
        t->last_len = m->data.len;
        t->last_type = m->type;
        t->binaries += m->type == UGO_WS_BINARY;
        if(m->reserved || (m->type != UGO_WS_TEXT && m->type != UGO_WS_BINARY))
            t->late++;
        if(m->data.len)
            memcpy(t->last, data, m->data.len < sizeof(t->last) ? m->data.len : sizeof(t->last));
        if(t->check)
            t->check(t, data, (size_t)m->data.len);
        if(t->release && ugo_ws_release(c, t->id, m->seq) != UGO_OK)
            t->bad++;
    } else if(e->kind == UGO_EV_COMPLETED) {
        t->completed++;
        t->done = e->u.completed;
    } else {
        t->late++;
    }
}

enum { UNTIL_OPEN, UNTIL_DONE };

static int ws_pump(ugo_client *c, ws_track *t, int until, int messages, int timeout_ms)
{
    int64_t end = ugo_monotonic_us() + (int64_t)timeout_ms * 1000;
    for(;;) {
        if(t->completed || (until == UNTIL_OPEN && t->headers && t->messages >= messages))
            return 1;
        if(ugo_monotonic_us() > end)
            return 0;
        ugo_event events[8];
        int32_t k = ugo_wait_events(c, events, 8, 50);
        if(k < 0)
            return 0;
        for(int i = 0; i < k; ++i)
            ws_apply(c, t, &events[i]);
    }
}

static int ws_open_track(ugo_client *c, const ugo_ws_request *r, ws_track *t)
{
    memset(t, 0, sizeof(*t));
    t->release = true;
    return ugo_ws_open(c, r, &t->id) == UGO_OK;
}

static int last_is(const ws_track *t, const char *s)
{
    return t->last_len == strlen(s) && !memcmp(t->last, s, t->last_len);
}

static long long ws_log_within(const char *url, const char *key, char *string, size_t size, int wait_ms);

static long long ws_log(const char *url, const char *key, char *string, size_t size)
{
    return ws_log_within(url, key, string, size, 5000);
}

/* Value of `key` in the bench's log line for this WebSocket, waiting up to wait_ms for the server to write it. */
static long long ws_log_within(const char *url, const char *key, char *string, size_t size, int wait_ms)
{
    const char *target = strstr(strstr(url, "://") + 3, "/");
    char path[1024], needle[600], field[64];
    snprintf(path, sizeof(path), "%s/requests.jsonl", run_dir);
    snprintf(needle, sizeof(needle), "{\"ws_path\": \"%s\", \"%s\": ", target, key);
    int64_t end = ugo_monotonic_us() + (int64_t)wait_ms * 1000;
    do {
        size_t len;
        char *log = read_file(path, &len);
        char *hit = log ? strstr(log, needle) : NULL;
        if(hit) {
            const char *v = hit + strlen(needle);
            long long n = -1;
            if(*v == '"') {
                const char *close = strchr(v + 1, '"');
                size_t k = close ? (size_t)(close - v - 1) : 0;
                if(string && size) {
                    if(k >= size)
                        k = size - 1;
                    memcpy(string, v + 1, k);
                    string[k] = 0;
                }
                n = (long long)k;
            } else {
                snprintf(field, sizeof(field), "%.40s", v);
                field[strcspn(field, ",}")] = 0;
                n = strtoll(field, NULL, 10);
                if(string && size)
                    snprintf(string, size, "%s", field);
            }
            free(log);
            return n;
        }
        free(log);
        sleep_ms(20);
    } while(ugo_monotonic_us() < end);
    return -1;
}

static void ws_echo_over(const char *base)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], tagged[64];
    snprintf(tagged, sizeof(tagged), "echo?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), base, tagged));
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"early", 5), UGO_E_STATE);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.h.http_status, 101);
    CHECK_EQ(t.h.http_version, UGO_HTTP_1_1);
    CHECK(t.h.headers.len > 12 && !memcmp((const void *)(uintptr_t)t.h.headers.ptr, "HTTP/1.1 101", 12));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"hello", 5), UGO_OK);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, NULL, 0), UGO_OK);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"\xd0\xb6", 2), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 3, 10000));
    CHECK_EQ(t.messages, 3);
    CHECK(last_is(&t, "\xd0\xb6"));
    CHECK_EQ(t.bytes, 7);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_E_STATE);
    CHECK_EQ(ugo_release(c, t.id), UGO_E_STATE);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, (const uint8_t *)"bye", 3), UGO_OK);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_E_STATE);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"late", 4), UGO_E_STATE);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK(quiet(c, 100));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.bad, 0);
    CHECK_EQ(t.completed, 1);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.ws_close_code, 1000);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_LOCAL);
    CHECK_EQ(t.done.http_status, 101);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(res.error_message.len == 3 && !memcmp((const void *)(uintptr_t)res.error_message.ptr, "bye", 3));
    CHECK(res.headers.len > 0);
    CHECK_EQ(ws_log(url, "ws_close", NULL, 0), 1000);
    char names[512];
    const char *target = strstr(strstr(url, "://") + 3, "/");
    header_names(target, names, sizeof(names));
    CHECK(strstr(names, "authorization") && strstr(names, "sec-websocket-key") && strstr(names, "upgrade"));
    CHECK(!strstr(names, "accept,") && !strstr(names, "user-agent"));
    CHECK_EQ(ugo_ws_release(c, t.id, t.seq), UGO_OK);
    CHECK_EQ(ugo_ws_release(c, t.id, t.seq + 1), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    CHECK_EQ(ugo_ws_release(c, t.id, 0), UGO_E_STALE_ID);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, NULL, 0), UGO_E_STALE_ID);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_E_STALE_ID);
    ugo_client_destroy(c);
}

static void test_tls12(void)
{
    if(!tls12_base) {
        skipped = "UGO_TEST_TLS12 unset";
        return;
    }
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    ugo_request r = get_request(make_url(url, sizeof(url), tls12_base, "/big/300000"));
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_status, 200);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK_EQ(res.body.len, 300000);
    const uint8_t *b = (const uint8_t *)(uintptr_t)res.body.ptr;
    for(int k = 0; k < 300000; ++k)
        if(b[k] != k % 256)
            FAIL("body byte %d", k);
    ugo_client_destroy(c);
    ws_echo_over(tls12_base);
}

static void test_ws_echo(void)
{
    ws_echo_over(http_base);
    if(!failure[0])
        ws_echo_over(https_base);
    if(!failure[0] && https1_base)
        ws_echo_over(https1_base);
}

static void ws_expect_failure(ugo_client *c, const char *path, uint32_t cls, uint32_t detail, int headers, const char *auth,
                              ws_track *t)
{
    char url[512];
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    if(auth)
        r.headers = text(auth);
    r.open_timeout_ms = 500;
    CHECK(ws_open_track(c, &r, t));
    if(!ws_pump(c, t, UNTIL_DONE, 0, 10000))
        FAIL("%s did not complete: headers %d", path, t->headers);
    CHECK(quiet(c, 50));
    CHECK_EQ(t->late, 0);
    CHECK_EQ(t->completed, 1);
    CHECK_EQ(t->headers, headers);
    CHECK_EQ(t->done.error_class, cls);
    CHECK_EQ(t->done.detail, detail);
    CHECK_EQ(t->done.ws_close_code, 1006);
}

static void test_ws_handshake_failures(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    ws_track t;
    ugo_result res;
    ws_expect_failure(c, "echo", UGO_CLASS_HTTP_STATUS, UGO_DETAIL_NONE, 1, "X-No-Auth: 1", &t);
    CHECK_EQ(t.h.http_status, 401);
    CHECK_EQ(t.done.http_status, 401);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    const char *bad[] = {"bad-accept", "missing-accept", "extension", "extension?value=~", "duplicate-accept",
                         "echo?protocol=chat", "echo?protocol=other", "echo?protocol=~", "echo?protocol=chat&protocol=chat",
                         "echo?protocol=CHAT", "echo?protocol=chat,superchat"};
    const char *offered = "Authorization: Bearer bench-token\r\nSec-WebSocket-Protocol: chat, superchat\r\n";
    for(size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i) {
        ws_expect_failure(c, bad[i], UGO_CLASS_TRANSFER, UGO_DETAIL_WS_HANDSHAKE, 0, i > 5 ? offered : NULL, &t);
        CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
        CHECK(res.headers.len > 12 && !memcmp((const void *)(uintptr_t)res.headers.ptr, "HTTP/1.1 101", 12));
        CHECK(res.error_message.len > 0);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ws_expect_failure(c, "stall-handshake", UGO_CLASS_TIMEOUT, UGO_DETAIL_NONE, 0, NULL, &t);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    char url[512];
    const char *chosen[] = {"superchat", "chat"};
    for(int i = 0; i < 2; ++i) {
        char path[64];
        snprintf(path, sizeof(path), "echo?protocol=%s&tag=%d", chosen[i], next_tag());
        ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
        r.headers = text(i ? "Authorization: Bearer bench-token\nSec-WebSocket-Protocol: superchat\nSec-WebSocket-Protocol: chat\n"
                           : offered);
        CHECK(ws_open_track(c, &r, &t));
        CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
        CHECK_EQ(t.h.http_status, 101);
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

static char *ws_redirect_url(char *out, size_t size, const char *base, const char *query, const char *to)
{
    char path[512];
    size_t n = (size_t)snprintf(path, sizeof(path), "redirect?%s%sto=", query, *query ? "&" : "");
    for(; *to && n + 4 < sizeof(path); ++to)
        n += isalnum((unsigned char)*to) ? (size_t)snprintf(path + n, 2, "%c", *to)
                                         : (size_t)snprintf(path + n, 4, "%%%02X", (unsigned char)*to);
    return ws_url(out, size, base, path);
}

static void ws_redirect_open(ugo_client *c, const char *url, const char *final_target, uint32_t hops)
{
    ugo_ws_request r = ws_request(url);
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    if(!ws_pump(c, &t, UNTIL_OPEN, 0, 10000))
        FAIL("%s did not open: class %u status %u", url, t.done.error_class, t.done.http_status);
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.h.http_status, 101);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"hop", 3), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
    CHECK(last_is(&t, "hop"));
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_status, 101);
    CHECK_EQ(t.done.redirect_count, hops);
    CHECK(t.done.redirect_us > 0);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(res.headers.len > 12 && !memcmp((const void *)(uintptr_t)res.headers.ptr, "HTTP/1.1 101", 12));
    CHECK(recorded_has(final_target, "authorization: bearer bench-token"));
    CHECK_EQ(ugo_ws_release(c, t.id, t.seq), UGO_OK);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
}

static void ws_redirect_refused(ugo_client *c, const char *url, uint32_t cls, int32_t curl_code, uint32_t status,
                                uint32_t hops, ws_track *t)
{
    ugo_ws_request r = ws_request(url);
    CHECK(ws_open_track(c, &r, t));
    if(!ws_pump(c, t, UNTIL_DONE, 0, 10000))
        FAIL("%s did not complete", url);
    CHECK(quiet(c, 50));
    CHECK_EQ(t->completed, 1);
    CHECK_EQ(t->headers, cls == UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t->done.error_class, cls);
    CHECK_EQ(t->done.curl_code, curl_code);
    CHECK_EQ(t->done.http_status, status);
    CHECK_EQ(t->done.redirect_count, hops);
    CHECK_EQ(t->done.ws_close_code, 1006);
}

static void test_ws_redirects(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[1024], to[256], query[64], target[64];
    const int codes[] = {301, 302, 303, 307, 308};
    for(int i = 0; i < 5; ++i) {
        snprintf(target, sizeof(target), "/ws/echo?tag=%d", next_tag());
        snprintf(query, sizeof(query), "code=%d", codes[i]);
        ws_redirect_open(c, ws_redirect_url(url, sizeof(url), http_base, query, target), target, 1);
        if(failure[0])
            return;
    }
    snprintf(target, sizeof(target), "/ws/echo?tag=%d", next_tag());
    snprintf(to, sizeof(to), "%s%s", https_base, target);
    ws_redirect_open(c, ws_redirect_url(url, sizeof(url), https_base, "", to), target, 1);
    snprintf(target, sizeof(target), "/ws/echo?tag=%d", next_tag());
    ws_redirect_open(c, ws_redirect_url(url, sizeof(url), http_base, "hops=10", target), target, 10);
    if(failure[0])
        return;

    ws_track t;
    ugo_result res;
    const char *bases[] = {http_base, https_base};
    for(int b = 0; b < 2; ++b) {
        char cross[256], middle[512], destination[768], want[300];
        const char *ip = strstr(bases[b], "127.0.0.1");
        CHECK(ip);
        snprintf(cross, sizeof(cross), "%.*slocalhost%s", (int)(ip - bases[b]), bases[b], ip + 9);
        for(int k = 0; k < 3; ++k) {
            snprintf(target, sizeof(target), "/ws/echo?tag=%d", next_tag());
            if(k == 2) {
                snprintf(to, sizeof(to), "%s%s", bases[b], target);
                redirect_to(middle, sizeof(middle), 302, to);
                snprintf(destination, sizeof(destination), "%s%s", cross, middle);
            } else {
                snprintf(destination, sizeof(destination), "%s%s", k ? cross : bases[b], target);
            }
            ugo_ws_request r = ws_request(ws_redirect_url(url, sizeof(url), bases[b], "", destination));
            r.headers = text("Authorization: Bearer bench-token\r\nCookie: session=1\r\nHost: original.example\r\n");
            CHECK(ws_open_track(c, &r, &t));
            CHECK(ws_pump(c, &t, k == 1 ? UNTIL_DONE : UNTIL_OPEN, 0, 10000));
            if(k == 2) {
                snprintf(want, sizeof(want), "\"host: %s\"", strstr(cross, "://") + 3);
                CHECK(recorded_has(middle, want));
                CHECK(!recorded_has(middle, "authorization:"));
                CHECK(!recorded_has(middle, "cookie:"));
            }
            snprintf(want, sizeof(want), "\"host: %s\"", k == 1 ? strstr(cross, "://") + 3 : "original.example");
            CHECK(recorded_has(target, want));
            CHECK_EQ(recorded_has(target, "authorization: bearer bench-token"), k != 1);
            CHECK_EQ(recorded_has(target, "cookie: session=1"), k != 1);
            if(k == 1) {
                CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
                CHECK_EQ(t.done.http_status, 401);
            } else {
                CHECK_EQ(t.h.http_status, 101);
                CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
                CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
                CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
            }
            CHECK_EQ(t.done.redirect_count, k == 2 ? 2 : 1);
            CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        }
    }
    ws_redirect_refused(c, ws_redirect_url(url, sizeof(url), http_base, "hops=11", "/ws/echo"), UGO_CLASS_TRANSFER,
                        47, 302, 10, &t);
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(memmem((const void *)(uintptr_t)res.headers.ptr, (size_t)res.headers.len, "Location: /ws/echo\r\n", 20));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ws_redirect_refused(c, ws_redirect_url(url, sizeof(url), http_base, "", "ftp://127.0.0.1/ws"), UGO_CLASS_TRANSFER,
                        1, 302, 0, &t);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    /* wss -> ws is followed as https -> http is; another origin gets no Authorization, so the bench answers 401. */
    const char *other[] = {http_base, https1_base};
    for(int i = 0; i < 2 && other[i]; ++i) {
        snprintf(target, sizeof(target), "/ws/echo?tag=%d", next_tag());
        snprintf(to, sizeof(to), "%s%s", other[i], target);
        ws_redirect_refused(c, ws_redirect_url(url, sizeof(url), https_base, "", to), UGO_CLASS_HTTP_STATUS,
                            22, 401, 1, &t);
        CHECK_EQ(t.h.http_status, 401);
        char *line = recorded(target);
        bool dropped = line && !strcasestr(line, "authorization");
        free(line);
        CHECK(dropped);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

static void test_ws_control_frames(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    const char *modes[] = {"ping", "split-ping", "fragmented"};
    const char *pongs[] = {"server-ping", "split-ping", "between"};
    for(int i = 0; i < 3; ++i) {
        char url[512], path[64], pong[64];
        snprintf(path, sizeof(path), "%s?tag=%d", modes[i], next_tag());
        ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
        ws_track t;
        CHECK(ws_open_track(c, &r, &t));
        CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
        CHECK_EQ(ws_log(url, "ws_pong", pong, sizeof(pong)), (long long)strlen(pongs[i]));
        CHECK(!strcmp(pong, pongs[i]));
        if(i == 2) {
            CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
            CHECK(last_is(&t, "frag-mented"));
        }
        CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"after", 5), UGO_OK);
        CHECK(ws_pump(c, &t, UNTIL_OPEN, i == 2 ? 2 : 1, 10000));
        CHECK(last_is(&t, "after"));
        CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        CHECK_EQ(t.late, 0);
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

static void test_ws_server_close(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[64];
    snprintf(path, sizeof(path), "close?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK(quiet(c, 50));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.headers, 1);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.ws_close_code, 1000);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_REMOTE);
    ugo_result res;
    CHECK_EQ(ugo_get_result(c, t.id, &res), UGO_OK);
    CHECK(res.error_message.len == 10 && !memcmp((const void *)(uintptr_t)res.error_message.ptr, "bench done", 10));
    CHECK_EQ(ws_log(url, "ws_close", NULL, 0), 1000);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_E_STATE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static int result_error_is(ugo_client *c, uint64_t id, const char *text, size_t len)
{
    ugo_result res;
    return ugo_get_result(c, id, &res) == UGO_OK && res.error_message.len == len &&
           !memcmp((const void *)(uintptr_t)res.error_message.ptr, text, len);
}

static void test_ws_close_reasons(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[96];
    ws_track t;
    snprintf(path, sizeof(path), "echo?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, (const uint8_t *)"a\0b", 3), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_LOCAL);
    CHECK(result_error_is(c, t.id, "a\0b", 3));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    snprintf(path, sizeof(path), "close?payload=03e8610062&tag=%d", next_tag());
    r = ws_request(ws_url(url, sizeof(url), http_base, path));
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.ws_close_code, 1000);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_REMOTE);
    CHECK(result_error_is(c, t.id, "a\0b", 3));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    const char *replies[] = {"58", "03ed", "03e8ff"};
    for(int i = 0; i < 3; ++i) {
        snprintf(path, sizeof(path), "bad-close-reply?payload=%s&tag=%d", replies[i], next_tag());
        r = ws_request(ws_url(url, sizeof(url), http_base, path));
        CHECK(ws_open_track(c, &r, &t));
        CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
        CHECK_EQ(ugo_ws_close(c, t.id, 1000, (const uint8_t *)"bye", 3), UGO_OK);
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        CHECK_EQ(t.late, 0);
        if(t.done.error_class != UGO_CLASS_TRANSFER)
            FAIL("close reply %s: class %u", replies[i], t.done.error_class);
        CHECK_EQ(t.done.ws_close_code, 1000);
        CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_LOCAL);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

static void check_pattern(ws_track *t, const uint8_t *data, size_t len)
{
    if(t->seq % 16 != 1 && len > 4096) {
        if(data[len - 1] != (uint8_t)WS_PATTERN[(len - 1) % 36])
            t->bad++;
        len = 4096;
    }
    char index[9];
    snprintf(index, sizeof(index), "%08llu", (unsigned long long)(t->seq - 1));
    if(len < 8 || memcmp(data, index, 8))
        t->bad++;
    for(size_t k = 8; k < len; ++k)
        if(data[k] != (uint8_t)WS_PATTERN[k % 36]) {
            t->bad++;
            break;
        }
}

static void ws_protocol_error(ugo_client *c, const char *mode, uint32_t max_message, uint32_t code)
{
    char url[512], path[64];
    snprintf(path, sizeof(path), "%s%ctag=%d", mode, strchr(mode, '?') ? '&' : '?', next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    r.max_message_bytes = max_message;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.messages, 0);
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.ws_close_code, code);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_LOCAL);
    CHECK_EQ(ws_log(url, "ws_close", NULL, 0), code);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
}

static void test_ws_big_and_protocol_errors(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, "big?bytes=70000"));
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    t.check = check_pattern;
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
    CHECK_EQ(t.last_len, 70000);
    CHECK_EQ(t.bad, 0);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ws_protocol_error(c, "big?bytes=70000", 69999, 1009);
    ws_protocol_error(c, "bad-utf8", 1 << 20, 1007);
    ugo_client_destroy(c);
}

static int last_bytes(const ws_track *t, uint32_t type, const void *data, size_t len)
{
    return t->last_type == type && t->last_len == len && (!len || !memcmp(t->last, data, len));
}

static void ws_binary_over(const char *base)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[64];
    snprintf(path, sizeof(path), "echo?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), base, path));
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    const uint8_t raw[] = {0x00, 0xff, 0xc0, 0x80, 'b'};
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, raw, sizeof(raw)), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_send(c, t.id, 0, raw, sizeof(raw)), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_send(c, t.id, 3, raw, sizeof(raw)), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_BINARY, raw, sizeof(raw)), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
    CHECK(last_bytes(&t, UGO_WS_BINARY, raw, sizeof(raw)));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"text", 4), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 2, 10000));
    CHECK(last_bytes(&t, UGO_WS_TEXT, "text", 4));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_BINARY, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 3, 10000));
    CHECK(last_bytes(&t, UGO_WS_BINARY, NULL, 0));
    size_t n = 70000;
    uint8_t *big = pattern(n);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_BINARY, big, (uint32_t)n), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 4, 10000));
    CHECK_EQ(t.last_type, UGO_WS_BINARY);
    CHECK_EQ(t.last_len, n);
    CHECK(!memcmp(t.last, big, sizeof(t.last)));
    free(big);
    CHECK_EQ(t.binaries, 3);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    const char *modes[] = {"binary", "fragmented-binary"};
    const char *expect[] = {"\x00\x01" "binary\xff", "\x00" "frag\xff" "mented"};
    const size_t lens[] = {9, 12};
    for(int i = 0; i < 2; ++i) {
        snprintf(path, sizeof(path), "%s?tag=%d", modes[i], next_tag());
        r = ws_request(ws_url(url, sizeof(url), base, path));
        CHECK(ws_open_track(c, &r, &t));
        CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
        if(!last_bytes(&t, UGO_WS_BINARY, expect[i], lens[i]))
            FAIL("%s: type %u, %zu bytes", modes[i], t.last_type, t.last_len);
        if(i == 1) {
            char pong[32];
            CHECK_EQ(ws_log(url, "ws_pong", pong, sizeof(pong)), 7);
            CHECK(!strcmp(pong, "between"));
        }
        CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        CHECK_EQ(t.messages, 1);
        CHECK_EQ(t.late, 0);
        CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

static void test_ws_binary(void)
{
    ws_binary_over(http_base);
    if(!failure[0])
        ws_binary_over(https_base);
}

static void test_ws_text_validated(void)
{
    ugo_client *c = client_new(1);
    CHECK(c);
    char url[512], path[64];
    snprintf(path, sizeof(path), "echo?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    const char *bad[] = {"\xc0\x80", "\xed\xa0\x80", "\xf4\x90\x80\x80", "ok\xe2\x82", "\x80", "\xff"};
    for(size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i)
        if(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)bad[i], (uint32_t)strlen(bad[i])) != UGO_E_INVALID_ARG)
            FAIL("invalid text %zu accepted", i);
    const char *good = "\xf0\x9f\x98\x80\xe2\x82\xac\x7f";
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)good, (uint32_t)strlen(good)), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 10000));
    CHECK(last_bytes(&t, UGO_WS_TEXT, good, strlen(good)));
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.late, 0);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_ws_protocol_error_stops_reading(void)
{
    ugo_client *c = client_new(1);
    CHECK(c);
    /* Each server write ends in a frame curl's decoder refuses: reading past the shim's error would surface that error. */
    static const struct {
        const char *mode;
        uint32_t max_message;
        uint32_t sent;
        uint32_t first;
        uint32_t detail;
    } cases[] = {{"bad-utf8?then=after", 1u << 20, 1007, 1007, UGO_DETAIL_WS_CLOSED_BY_LOCAL},
                 {"fragmented-binary", 8, 1009, 1009, UGO_DETAIL_WS_CLOSED_BY_LOCAL},
                 {"close?payload=58", 1u << 20, 1002, 1005, UGO_DETAIL_WS_CLOSED_BY_REMOTE}};
    for(size_t i = 0; i < sizeof(cases) / sizeof(cases[0]); ++i) {
        char url[512], path[128];
        snprintf(path, sizeof(path), "%s%cjunk=1&silent=1&tag=%d", cases[i].mode, strchr(cases[i].mode, '?') ? '&' : '?',
                 next_tag());
        ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
        r.max_message_bytes = cases[i].max_message;
        r.close_timeout_ms = 5000;
        ws_track t;
        CHECK(ws_open_track(c, &r, &t));
        int64_t started = ugo_monotonic_us();
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        int64_t spent_ms = (ugo_monotonic_us() - started) / 1000;
        CHECK_EQ(t.late, 0);
        CHECK_EQ(t.messages, 0);
        if(t.done.error_class != UGO_CLASS_TRANSFER || t.done.ws_close_code != cases[i].first ||
           t.done.detail != cases[i].detail)
            FAIL("%s: class %u close %u detail %u curl %d", cases[i].mode, t.done.error_class, t.done.ws_close_code,
                 t.done.detail, t.done.curl_code);
        if(spent_ms >= 2500)
            FAIL("%s: ended after %lld ms: waited for the server's CLOSE", cases[i].mode, (long long)spent_ms);
        CHECK_EQ(ws_log(url, "ws_close", NULL, 0), cases[i].sent);
        CHECK_EQ(ws_log(url, "ws_after", NULL, 0), 0);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        CHECK(!failure[0]);
    }
    ugo_client_destroy(c);
}

static void test_ws_partial_send(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    static uint8_t big[16u << 20];
    size_t size = 4u << 20;
    for(size_t k = 0; k < sizeof(big); ++k)
        big[k] = (uint8_t)WS_PATTERN[k % 36];
    memcpy(big, "00000000", 8);
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, "echo"));
    r.max_message_bytes = (uint32_t)size;
    r.max_backlog_bytes = size;
    r.max_send_bytes = (uint32_t)size + 8;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, big, (uint32_t)size + 1), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, big, (uint32_t)size), UGO_OK);
    t.check = check_pattern;
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 20000));
    CHECK_EQ(t.last_len, size);
    CHECK_EQ(t.bad, 0);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    size = sizeof(big);
    r = ws_request(ws_url(url, sizeof(url), http_base, "slow-read"));
    r.max_message_bytes = (uint32_t)size;
    r.max_backlog_bytes = size;
    r.max_send_bytes = (uint32_t)size + 8;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    unsigned partial = __atomic_load_n(&ugo_test_ws_partial_sends, __ATOMIC_RELAXED);
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, big, (uint32_t)size), UGO_OK);
    t.check = check_pattern;
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 1, 30000));
    CHECK_EQ(t.last_len, size);
    CHECK_EQ(t.bad, 0);
    CHECK(__atomic_load_n(&ugo_test_ws_partial_sends, __ATOMIC_RELAXED) > partial);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r = ws_request(ws_url(url, sizeof(url), http_base, "stall-read"));
    r.max_send_bytes = 1 << 20;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    int full = 0;
    int64_t end = ugo_monotonic_us() + 20000000;
    while(!full && ugo_monotonic_us() < end) {
        int32_t rc = ugo_ws_send(c, t.id, UGO_WS_TEXT, big, 65536);
        if(rc == UGO_E_CAPACITY)
            full = 1;
        else if(rc != UGO_OK)
            FAIL("ws_send returned %d", rc);
        else
            sleep_ms(1);
    }
    CHECK(full);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.done.detail, UGO_DETAIL_CALLER);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_ws_slow_consumer(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[96];
    int count = 256;
    snprintf(path, sizeof(path), "flood?count=%d&size=65000&tag=%d", count, next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    r.max_message_bytes = 65000;
    r.max_backlog_bytes = 4 * 65536;
    r.max_backlog_count = 4;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    t.release = false;
    t.check = check_pattern;
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 4, 10000));
    CHECK(quiet(c, 1000));
    CHECK_EQ(t.messages, 4);
    CHECK_EQ(ugo_ws_release(c, t.id, 5), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_release(c, t.id, 2), UGO_OK);
    CHECK_EQ(ugo_ws_release(c, t.id, 1), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 6, 10000));
    CHECK(quiet(c, 200));
    CHECK_EQ(t.messages, 6);
    t.release = true;
    CHECK_EQ(ugo_ws_release(c, t.id, t.seq), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_OPEN, count, 30000));
    CHECK_EQ(t.messages, count);
    CHECK_EQ(t.bad, 0);
    CHECK_EQ(t.late, 0);
    char seconds[64];
    CHECK(ws_log(url, "ws_flood", seconds, sizeof(seconds)) >= 0);
    if(strtod(seconds, NULL) < 0.9)
        FAIL("the flood finished in %s s: the server was not held back", seconds);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_ws_close_timeout(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], path[64];
    snprintf(path, sizeof(path), "no-close-reply?tag=%d", next_tag());
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
    r.close_timeout_ms = 300;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    int64_t start = ugo_monotonic_us();
    CHECK_EQ(ugo_ws_close(c, t.id, 4000, (const uint8_t *)"app", 3), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    int64_t took = ugo_monotonic_us() - start;
    CHECK(took >= 280000 && took < 3000000);
    CHECK_EQ(t.done.error_class, UGO_CLASS_TIMEOUT);
    CHECK_EQ(t.done.ws_close_code, 4000);
    CHECK_EQ(t.done.detail, UGO_DETAIL_WS_CLOSED_BY_LOCAL);
    CHECK_EQ(ws_log(url, "ws_close", NULL, 0), 4000);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
}

static void test_ws_pong_flood(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    for(int i = 0; i < 2; ++i) {
        char url[512], path[64], http_url[512];
        snprintf(path, sizeof(path), "pong-flood?tag=%d", next_tag());
        ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
        r.close_timeout_ms = 300;
        ws_track t;
        CHECK(ws_open_track(c, &r, &t));
        CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
        sleep_ms(100);
        int64_t start = ugo_monotonic_us();
        if(i == 0) {
            ugo_request get = get_request(make_url(http_url, sizeof(http_url), http_base, "/ok"));
            track g;
            memset(&g, 0, sizeof(g));
            CHECK_EQ(ugo_submit(c, &get, &g.id), UGO_OK);
            if(!drain(c, &g, 1, 3000))
                FAIL("GET did not complete during the flood");
            CHECK_EQ(g.done.error_class, UGO_CLASS_NONE);
            CHECK_EQ(ugo_release(c, g.id), UGO_OK);
            start = ugo_monotonic_us();
            CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
        } else {
            CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        }
        if(!ws_pump(c, &t, UNTIL_DONE, 0, 3000))
            FAIL("%s did not complete during the flood", i ? "abort" : "close");
        int64_t took = ugo_monotonic_us() - start;
        CHECK(took < 3000000);
        CHECK_EQ(t.done.error_class, i ? UGO_CLASS_ABORTED : UGO_CLASS_TIMEOUT);
        if(!i)
            CHECK(took >= 280000);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }
    ugo_client_destroy(c);
}

struct ws_empty_sender {
    ugo_client *c;
    uint64_t id;
    unsigned sent;
    int32_t failed;
};

static void *ws_send_empty_thread(void *arg)
{
    struct ws_empty_sender *w = arg;
    for(;;) {
        int32_t rc = ugo_ws_send(w->c, w->id, UGO_WS_TEXT, NULL, 0);
        if(rc == UGO_OK)
            __atomic_fetch_add(&w->sent, 1u, __ATOMIC_RELAXED);
        else if(rc != UGO_E_CAPACITY) {
            w->failed = rc == UGO_E_STATE ? 0 : rc;
            return NULL;
        }
    }
}

static void test_ws_empty_send_flood(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512], http_url[512];
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, "discard"));
    r.max_send_bytes = 8192;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    struct ws_empty_sender sender = {c, t.id, 0, 0};
    pthread_t thread;
    CHECK_EQ(pthread_create(&thread, NULL, ws_send_empty_thread, &sender), 0);
    int64_t end = ugo_monotonic_us() + 3000000;
    while(__atomic_load_n(&sender.sent, __ATOMIC_RELAXED) < r.max_send_bytes / 4 && ugo_monotonic_us() < end)
        sleep_ms(1);
    ugo_request get = get_request(make_url(http_url, sizeof(http_url), http_base, "/ok"));
    track g = {0};
    int32_t submitted = ugo_submit(c, &get, &g.id);
    int got = submitted == UGO_OK && drain(c, &g, 1, 3000);
    int64_t start = ugo_monotonic_us();
    int32_t aborted = ugo_abort(c, t.id);
    int closed = ws_pump(c, &t, UNTIL_DONE, 0, 3000);
    int64_t took = ugo_monotonic_us() - start;
    pthread_join(thread, NULL);
    int32_t get_released = ugo_release(c, g.id);
    int32_t ws_released = ugo_release(c, t.id);
    ugo_client_destroy(c);
    CHECK(sender.sent >= r.max_send_bytes / 4);
    CHECK_EQ(sender.failed, 0);
    CHECK_EQ(submitted, UGO_OK);
    CHECK(got);
    CHECK_EQ(g.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(aborted, UGO_OK);
    CHECK(closed);
    CHECK(took < 3000000);
    CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(t.done.detail, UGO_DETAIL_CALLER);
    CHECK_EQ(get_released, UGO_OK);
    CHECK_EQ(ws_released, UGO_OK);
}

static void test_ws_abort(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    const char *modes[] = {"stall-handshake", "echo", "no-close-reply"};
    for(int i = 0; i < 3; ++i) {
        char url[512], path[64];
        snprintf(path, sizeof(path), "%s?tag=%d", modes[i], next_tag());
        ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, path));
        ws_track t;
        CHECK(ws_open_track(c, &r, &t));
        if(i)
            CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
        else
            sleep_ms(100);
        if(i == 2) {
            CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
            CHECK_EQ(ws_log(url, "ws_close", NULL, 0), 1000);
        }
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"x", 1), UGO_E_STATE);
        CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
        CHECK_EQ(ugo_abort(c, t.id), UGO_OK);
        CHECK(quiet(c, 100));
        CHECK_EQ(t.late, 0);
        CHECK_EQ(t.completed, 1);
        CHECK_EQ(t.headers, i ? 1 : 0);
        CHECK_EQ(t.done.error_class, UGO_CLASS_ABORTED);
        CHECK_EQ(t.done.detail, UGO_DETAIL_CALLER);
        CHECK_EQ(t.done.ws_close_code, i == 2 ? 1000 : 1006);
        if(i == 1)
            CHECK_EQ(ws_log_within(url, "ws_close", NULL, 0, 300), -1);
        CHECK_EQ(ugo_ws_release(c, t.id, 0), UGO_OK);
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        CHECK_EQ(ugo_abort(c, t.id), UGO_E_STALE_ID);
    }
    ugo_client_destroy(c);
}

static void test_ws_invalid_and_states(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    uint64_t id;
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, "stall-handshake"));
    ugo_ws_request bad = r;
    bad.connect_timeout_ms = 0;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.close_timeout_ms = 0;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_message_bytes = 0;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_backlog_count = 0;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_send_bytes = 8;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_backlog_bytes = bad.max_message_bytes - 1;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_backlog_bytes = UINT64_MAX;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.max_message_bytes = 1;
    bad.max_backlog_bytes = SIZE_MAX / 2;
    bad.max_backlog_count = 1;
    bad.max_send_bytes = 9;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
#if SIZE_MAX == UINT32_MAX
    bad.max_backlog_bytes = 1;
    bad.max_send_bytes = UINT32_MAX;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad.max_backlog_bytes = INT32_MAX;
    bad.max_send_bytes = INT32_MAX;
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
#endif
    bad = r;
    char http_url[512];
    bad.url = text(make_url(http_url, sizeof(http_url), http_base, "/ws/echo"));
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    bad = r;
    bad.headers = text("Authorization: Bearer bench-token\nsec-websocket-key: AAAAAAAAAAAAAAAAAAAAAA==");
    CHECK_EQ(ugo_ws_open(c, &bad, &id), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_open(c, NULL, &id), UGO_E_INVALID_ARG);

    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK_EQ(ugo_ws_send(c, t.id, UGO_WS_TEXT, (const uint8_t *)"x", 1), UGO_E_STATE);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_E_STATE);
    CHECK_EQ(ugo_ws_close(c, t.id, 1005, NULL, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_close(c, t.id, 999, NULL, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_close(c, t.id, 2000, NULL, 0), UGO_E_INVALID_ARG);
    uint8_t reason[124];
    memset(reason, 'r', sizeof(reason));
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, reason, 124), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, (const uint8_t *)"\xff", 1), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_release(c, t.id, 1), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_release(c, t.id, 0), UGO_OK);

    ugo_request get = get_request(make_url(http_url, sizeof(http_url), http_base, "/ok"));
    track h;
    CHECK(run_one(c, &get, &h));
    CHECK_EQ(ugo_ws_send(c, h.id, UGO_WS_TEXT, NULL, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_close(c, h.id, 1000, NULL, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_ws_release(c, h.id, 0), UGO_E_INVALID_ARG);
    CHECK_EQ(ugo_release(c, h.id), UGO_OK);

    ws_track open;
    char echo_url[512];
    ugo_ws_request er = ws_request(ws_url(echo_url, sizeof(echo_url), http_base, "echo"));
    CHECK(ws_open_track(c, &er, &open));
    CHECK(ws_pump(c, &open, UNTIL_OPEN, 0, 10000));
    CHECK_EQ(ugo_ws_open(c, &r, &id), UGO_E_CAPACITY);
    CHECK_EQ(ugo_client_shutdown(c), UGO_OK);
    CHECK_EQ(ugo_ws_send(c, open.id, UGO_WS_TEXT, NULL, 0), UGO_E_SHUTDOWN);
    CHECK_EQ(ugo_ws_close(c, open.id, 1000, NULL, 0), UGO_E_SHUTDOWN);
    CHECK_EQ(ugo_ws_open(c, &r, &id), UGO_E_SHUTDOWN);
    ugo_event events[8];
    int32_t k = ugo_wait_events(c, events, 8, 0);
    for(int i = 0; i < k; ++i) {
        if(events[i].id == open.id)
            ws_apply(c, &open, &events[i]);
        else
            ws_apply(c, &t, &events[i]);
    }
    CHECK_EQ(open.completed, 1);
    CHECK_EQ(open.done.error_class, UGO_CLASS_ABORTED);
    CHECK_EQ(open.done.detail, UGO_DETAIL_SHUTDOWN);
    CHECK_EQ(t.completed, 1);
    CHECK_EQ(t.done.detail, UGO_DETAIL_SHUTDOWN);
    CHECK_EQ(ugo_wait_events(c, events, 8, 0), UGO_E_SHUTDOWN);
    CHECK_EQ(ugo_release(c, open.id), UGO_OK);
    ugo_client_destroy(c);
}

struct ws_sender {
    ugo_client *c;
    uint64_t id;
    int count;
    int failed;
};

static void *ws_send_thread(void *arg)
{
    struct ws_sender *w = arg;
    for(int i = 0; i < w->count;) {
        char text_[32];
        int n = snprintf(text_, sizeof(text_), "%08d", i);
        int32_t rc = ugo_ws_send(w->c, w->id, UGO_WS_TEXT, (const uint8_t *)text_, (uint32_t)n);
        if(rc == UGO_OK)
            ++i;
        else if(rc == UGO_E_CAPACITY)
            sleep_ms(1);
        else {
            w->failed = rc;
            break;
        }
    }
    return NULL;
}

static void check_counter(ws_track *t, const uint8_t *data, size_t len)
{
    char want[16];
    int n = snprintf(want, sizeof(want), "%08llu", (unsigned long long)(t->seq - 1));
    if(len != (size_t)n || memcmp(data, want, len))
        t->bad++;
}

static void test_ws_concurrent_send(void)
{
    ugo_client *c = client_new(2);
    CHECK(c);
    char url[512];
    ugo_ws_request r = ws_request(ws_url(url, sizeof(url), http_base, "echo"));
    r.max_send_bytes = 250;
    r.max_backlog_count = 8;
    r.max_backlog_bytes = 4096;
    r.max_message_bytes = 64;
    ws_track t;
    CHECK(ws_open_track(c, &r, &t));
    CHECK(ws_pump(c, &t, UNTIL_OPEN, 0, 10000));
    t.check = check_counter;
    struct ws_sender sender = {c, t.id, 2000, 0};
    pthread_t thread;
    CHECK_EQ(pthread_create(&thread, NULL, ws_send_thread, &sender), 0);
    int ok = ws_pump(c, &t, UNTIL_OPEN, sender.count, 30000);
    pthread_join(thread, NULL);
    CHECK(ok);
    CHECK_EQ(sender.failed, 0);
    CHECK_EQ(t.messages, sender.count);
    CHECK_EQ(t.bad, 0);
    CHECK_EQ(t.late, 0);
    CHECK_EQ(ugo_ws_close(c, t.id, 1000, NULL, 0), UGO_OK);
    CHECK(ws_pump(c, &t, UNTIL_DONE, 0, 10000));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    ugo_client_destroy(c);
}

struct test {
    const char *name;
    void (*fn)(void);
};

/* Byte i of /range/<size>?version=v (the bench's pattern shifted by 7 per version). */
static uint8_t range_byte(size_t i, int version)
{
    return (uint8_t)((i + (size_t)version * 7) % 256);
}

static int write_range_prefix(const char *path, size_t len, int version)
{
    uint8_t *data = malloc(len ? len : 1);
    for(size_t i = 0; i < len; ++i)
        data[i] = range_byte(i, version);
    int rc = write_file(path, data, len);
    free(data);
    return rc;
}

static int range_file_is(const char *path, size_t size, int version)
{
    size_t len;
    uint8_t *data = (uint8_t *)read_file(path, &len);
    int same = data && len == size;
    for(size_t i = 0; same && i < len; ++i)
        same = data[i] == range_byte(i, version);
    free(data);
    return same;
}

static int recorded_has(const char *target, const char *field)
{
    char *line = recorded(target);
    int found = line && strcasestr(line, field) != NULL;
    free(line);
    return found;
}

static void resume_over(const char *base, size_t size, uint32_t version)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char url[512], path[512], target[128], cut_target[128];
    temp_path(path, sizeof(path), "resume.part");
    unlink(path);
    snprintf(cut_target, sizeof(cut_target), "/range/%zu?gzip=1&cut=%zu", size, size / 3);
    ugo_request r = get_request(make_url(url, sizeof(url), base, cut_target));
    r.sink_path = text(path);
    r.memory_limit = 0;
    r.flags = UGO_REQ_RESUME;
    track t;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.http_version, version);
    CHECK_EQ(t.h.resume_offset, 0);
    CHECK(range_file_is(path, size / 3, 0));
    CHECK(!recorded_has(cut_target, "range: bytes"));
    CHECK(recorded_has(cut_target, "accept-encoding: identity"));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    snprintf(target, sizeof(target), "/range/%zu?gzip=1", size);
    r.url = text(make_url(url, sizeof(url), base, target));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 206);
    CHECK_EQ(t.h.resume_offset, size / 3);
    CHECK_EQ(t.h.content_length, size - size / 3);
    CHECK_EQ(t.done.bytes_received, size - size / 3);
    CHECK_EQ(t.p.received, size);
    CHECK_EQ(t.p.receive_total, size);
    CHECK(range_file_is(path, size, 0));
    char range[64];
    snprintf(range, sizeof(range), "range: bytes=%zu-", size / 3);
    CHECK(recorded_has(target, range));
    CHECK(recorded_has(target, "accept-encoding: identity"));
    ugo_client_destroy(c);
    unlink(path);
}

static void test_resume_h1(void)
{
    resume_over(http_base, 300000, UGO_HTTP_1_1);
}

static void test_resume_h2(void)
{
    resume_over(https_base, 300001, UGO_HTTP_2);
}

static void resume_case_on(ugo_client *c, const char *base, const char *path_query, size_t have, int version,
                           const char *if_range, uint32_t flags, track *t, char *path, size_t path_size)
{
    char url[512];
    temp_path(path, path_size, "case.part");
    unlink(path);
    if(have != (size_t)-1)
        write_range_prefix(path, have, version);
    ugo_request r = get_request(make_url(url, sizeof(url), base, path_query));
    r.sink_path = text(path);
    r.memory_limit = 0;
    r.flags = UGO_REQ_RESUME | flags;
    char header[96];
    if(if_range) {
        snprintf(header, sizeof(header), "If-Range: %s", if_range);
        r.headers = text(header);
    }
    if(!run_one(c, &r, t))
        t->completed = 0;
}

static void resume_case(ugo_client *c, const char *query, size_t size, size_t have, int version, const char *if_range,
                        track *t, char *path, size_t path_size)
{
    char target[160];
    snprintf(target, sizeof(target), "/range/%zu%s", size, query);
    resume_case_on(c, http_base, target, have, version, if_range, 0, t, path, path_size);
}

static void test_resume_cases(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char path[512];
    track t;

    resume_case(c, "?mode=ignore", 5000, 1000, 3, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 200);
    CHECK_EQ(t.h.resume_offset, 0);
    CHECK_EQ(t.p.received, 5000);
    CHECK(range_file_is(path, 5000, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "?version=1", 6000, 2000, 0, "\"r6000v0\"", &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 200);
    CHECK(range_file_is(path, 6000, 1));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "?version=1", 6001, 2000, 1, "\"r6001v1\"", &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 206);
    CHECK_EQ(t.h.resume_offset, 2000);
    CHECK(range_file_is(path, 6001, 1));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "?mode=unknown-total", 6002, 2000, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 206);
    CHECK_EQ(t.p.received, 6002);
    CHECK_EQ(t.p.receive_total, 6002);
    CHECK(range_file_is(path, 6002, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "", 4000, 4000, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.http_status, 416);
    CHECK_EQ(t.h.resume_offset, 4000);
    CHECK(range_file_is(path, 4000, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    /* The error-body cap applies to a matching 416 too. */
    resume_case(c, "?mode=big-416", 4001, 4001, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK_EQ(t.done.detail, UGO_DETAIL_MEMORY_LIMIT);
    CHECK(range_file_is(path, 4001, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    struct { const char *query; size_t size, have; long long left; } bad[] = {
        {"?mode=wrong-start", 4002, 1000, -1},
        {"", 4002, 4500, -1},
        {"?mode=bare-416", 4002, 4002, 4002},
        {"?mode=early-416", 4002, 1000, 1000},
        {"?mode=no-content-range", 4002, 1000, 1000},
        {"?mode=dup-range", 4002, 1000, 1000},
        {"?mode=dup-garbage", 4002, 1000, 1000},
        {"?mode=dup-range-rev", 4002, 1000, 1000},
        {"?mode=multipart", 4002, 1000, 1000},
        {"?mode=multipart-bare", 4002, 1000, 1000},
        {"?mode=multipart-then-plain", 4002, 1000, 1000},
        {"?mode=plain-then-multipart", 4002, 1000, 1000},
        {"?mode=huge-length", 4002, 1000, 1000},
        {"?mode=huge-length-chunked", 4002, 1000, 1000},
        {"?mode=length-short", 4002, 1000, 1000},
        {"?mode=length-long", 4002, 1000, 1000},
        {"?mode=chunked-short", 4002, 1000, 4000},
        {"?mode=chunked-long", 4002, 1000, 4002},
        {"?mode=no-length-short", 4002, 1000, 4000},
    };
    for(size_t i = 0; i < sizeof(bad) / sizeof(bad[0]); ++i) {
        resume_case(c, bad[i].query, bad[i].size, bad[i].have, 0, NULL, &t, path, sizeof(path));
        if(!t.completed || t.done.error_class != UGO_CLASS_TRANSFER || t.done.detail != UGO_DETAIL_RESUME_MISMATCH)
            FAIL("%s: class %u detail %u", bad[i].query, t.done.error_class, t.done.detail);
        CHECK(t.headers);
        if(t.progress && (t.p.receive_total < -1 || t.p.receive_total > (int64_t)bad[i].size))
            FAIL("%s: progress total %lld", bad[i].query, (long long)t.p.receive_total);
        if(bad[i].left < 0 ? file_size(path) != -1 : !range_file_is(path, (size_t)bad[i].left, 0))
            FAIL("%s: file size %lld", bad[i].query, file_size(path));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    }

    resume_case_on(c, http_base, "/range/4003?mode=wrong-start", 1000, 0, NULL, UGO_REQ_KEEP_PARTIAL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.detail, UGO_DETAIL_RESUME_MISMATCH);
    CHECK(range_file_is(path, 1000, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "?mode=wrong-total", 4004, 1000, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.detail, UGO_DETAIL_NONE);
    CHECK_EQ(t.p.receive_total, 4005);
    CHECK(range_file_is(path, 4004, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "?mode=short-range&chunk=1500", 4005, 1000, 0, NULL, &t, path, sizeof(path));
    int attempts = 1;
    while(t.completed && t.done.error_class == UGO_CLASS_TRANSFER && t.done.detail == UGO_DETAIL_NONE && attempts < 5) {
        CHECK_EQ(t.h.http_status, 206);
        CHECK(range_file_is(path, (size_t)(1000 + 1500 * attempts), 0));
        CHECK_EQ(ugo_release(c, t.id), UGO_OK);
        char url[512];
        ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/range/4005?mode=short-range&chunk=1500"));
        r.sink_path = text(path);
        r.flags = UGO_REQ_RESUME;
        CHECK(run_one(c, &r, &t));
        attempts++;
    }
    CHECK_EQ(attempts, 3);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK(range_file_is(path, 4005, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case_on(c, http_base, "/redirect/302?to=/range/4006", 1500, 0, NULL, 0, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.done.redirect_count, 1);
    CHECK_EQ(t.h.http_status, 206);
    CHECK_EQ(t.h.resume_offset, 1500);
    CHECK(range_file_is(path, 4006, 0));
    CHECK(recorded_has("/redirect/302?to=/range/4006", "range: bytes=1500-"));
    CHECK(recorded_has("/range/4006", "range: bytes=1500-"));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "", 3000, (size_t)-1, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK_EQ(t.h.http_status, 200);
    CHECK(range_file_is(path, 3000, 0));
    CHECK(!recorded_has("/range/3000", "range: bytes"));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    resume_case(c, "", 3001, 0, 0, NULL, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.error_class, UGO_CLASS_NONE);
    CHECK(range_file_is(path, 3001, 0));
    CHECK(!recorded_has("/range/3001", "range: bytes"));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    char url[512];
    temp_path(path, sizeof(path), "case.part");
    write_range_prefix(path, 700, 0);
    ugo_request r = get_request(make_url(url, sizeof(url), http_base, "/status/503"));
    r.sink_path = text(path);
    r.flags = UGO_REQ_RESUME;
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_HTTP_STATUS);
    CHECK(range_file_is(path, 700, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    r.url = text(make_url(url, sizeof(url), http_base, "/range/2000?cut=500"));
    CHECK(run_one(c, &r, &t));
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK(range_file_is(path, 1200, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);

    uint64_t id;
    r.method = text("HEAD");
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r.method = text("GET");
    r.headers = text("range: bytes=0-");
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    r.headers = text("");
    r.sink_path = text("");
    r.memory_limit = 1024;
    CHECK_EQ(ugo_submit(c, &r, &id), UGO_E_INVALID_ARG);
    ugo_client_destroy(c);
    unlink(path);
}

static void test_resume_h2_mismatch(void)
{
    ugo_client *c = client_new(4);
    CHECK(c);
    char path[512];
    track t;
    resume_case_on(c, https_base, "/range/5000?mode=wrong-start", 1000, 0, NULL, 0, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.http_version, UGO_HTTP_2);
    CHECK_EQ(t.done.detail, UGO_DETAIL_RESUME_MISMATCH);
    CHECK_EQ(file_size(path), -1);
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    resume_case_on(c, https_base, "/range/5001?mode=no-length-short", 1000, 0, NULL, 0, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.http_version, UGO_HTTP_2);
    CHECK_EQ(t.done.error_class, UGO_CLASS_TRANSFER);
    CHECK_EQ(t.done.detail, UGO_DETAIL_RESUME_MISMATCH);
    CHECK(range_file_is(path, 4999, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    resume_case_on(c, https_base, "/range/5002?mode=length-long", 1000, 0, NULL, 0, &t, path, sizeof(path));
    CHECK(t.completed);
    CHECK_EQ(t.done.detail, UGO_DETAIL_RESUME_MISMATCH);
    CHECK(range_file_is(path, 1000, 0));
    CHECK_EQ(ugo_release(c, t.id), UGO_OK);
    ugo_client_destroy(c);
    unlink(path);
}

static size_t unhex(const char *hex, unsigned char *out)
{
    size_t n = strlen(hex) / 2;
    for(size_t i = 0; i < n; ++i) {
        unsigned v;
        sscanf(hex + 2 * i, "%2x", &v);
        out[i] = (unsigned char)v;
    }
    return n;
}

static psa_key_id_t gcm_key(const unsigned char *key, size_t len)
{
    psa_key_attributes_t attributes = PSA_KEY_ATTRIBUTES_INIT;
    psa_set_key_usage_flags(&attributes, PSA_KEY_USAGE_ENCRYPT | PSA_KEY_USAGE_DECRYPT);
    psa_set_key_algorithm(&attributes, PSA_ALG_GCM);
    psa_set_key_type(&attributes, PSA_KEY_TYPE_AES);
    psa_key_id_t id = 0;
    if(psa_import_key(&attributes, key, len, &id) != PSA_SUCCESS)
        return 0;
    return id;
}

/* Lengths straddle the 64-byte runs the AArch64 four-block GCM patch takes; the digests come from the
 * unpatched mbedTLS. */
static void test_aes_gcm_known_answers(void)
{
    static const struct {
        const char *key, *aad, *plain, *cipher_tag;
    } vectors[] = {
        {"feffe9928665731c6d6a8f9467308308", "",
         "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525"
         "b16aedf5aa0de657ba637b391aafd255",
         "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa05"
         "1ba30b396a0aac973d58e091473f59854d5c2af327cd64a62cf35abd2ba6fab4"},
        {"feffe9928665731c6d6a8f9467308308", "feedfacedeadbeeffeedfacedeadbeefabaddad2",
         "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525"
         "b16aedf5aa0de657ba637b39",
         "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa05"
         "1ba30b396a0aac973d58e0915bc94fbc3221a5db94fae95ae7121a47"},
        {"feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308", "",
         "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525"
         "b16aedf5aa0de657ba637b391aafd255",
         "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838"
         "c5f61e6393ba7a0abcc9f662898015adb094dac5d93471bdec1a502270e3cc6c"},
        {"feffe9928665731c6d6a8f9467308308feffe9928665731c6d6a8f9467308308",
         "feedfacedeadbeeffeedfacedeadbeefabaddad2",
         "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525"
         "b16aedf5aa0de657ba637b39",
         "522dc1f099567d07f47f37a32a84427d643a8cdcbfe5c0c97598a2bd2555d1aa8cb08e48590dbb3da7b08b1056828838"
         "c5f61e6393ba7a0abcc9f66276fc6ece0f4e1768cddf8853bb2d551b"},
    };
    static const size_t lengths[] = {63, 64, 65, 127, 128, 129, 16384};
    static const char *const digests[2][7] = {
        {
            "6364f3bec0394c1b2f31ae22b99347c5af1ccc45cb7038322d728f93b195676e",
            "3ba01bed857ed354170eb44344d03b71f7e99dd297c530b76b70d80161d90eef",
            "c38d6a5dba9efe41339ac4faea2e98a2038b7de0243f28faa7f0f64a35bdf756",
            "54017688aa50726c598fe27d8ceb2c680c9f84f4509239cfc7664452012700b2",
            "004b118efb39dc4598095f22df4f5f007f54245904b47988749386815195b3e9",
            "7f46ca5009e795b8f530d81ed47a1e5e8d8b2645ee53181aee2418e7504ac795",
            "66fece2eeff90179f132884db1d19409ef85ebf023017493451c4869e7dd26bf",
        },
        {
            "12ac8246faadf350370fae4a48209c90f68f7a16db874a8de36389a966cd52c6",
            "adc4f2f53e7e8ef588b8c090aa3f10d47d661a3f9c5e01c0ad502db7225b7057",
            "b40b5be293ab3788427b38d68505fdc70b2b2c9005afbfeb9c0dc7be80cc3cb5",
            "6001612a1c62be6171575273b5fa9e560360d2891498ffdb741721e7a1982848",
            "517a82d23847ee9c47b6c3e215b5b2dc1a0a46faf957280bf0cef5ec9ed8ce48",
            "13f3b0da74619dbcebcdb84cdb495d18369e96f046635b73db3e0082af1b02fd",
            "fb0c51f1c4af743cf27fc9e55710873bcf4d968bea50b98da22c3d708fc47c65",
        },
    };
    static unsigned char buf[16384 + 16];
    unsigned char key[32], aad[20], plain[64], expected[80], nonce[12];
    CHECK_EQ(psa_crypto_init(), PSA_SUCCESS);
    unhex("cafebabefacedbaddecaf888", nonce);
    for(size_t v = 0; v < sizeof(vectors) / sizeof(vectors[0]); ++v) {
        psa_key_id_t id = gcm_key(key, unhex(vectors[v].key, key));
        size_t aad_len = unhex(vectors[v].aad, aad), len = unhex(vectors[v].plain, plain);
        unhex(vectors[v].cipher_tag, expected);
        memcpy(buf, plain, len);
        size_t out_len = 0;
        psa_status_t encrypted = psa_aead_encrypt(id, PSA_ALG_GCM, nonce, sizeof(nonce), aad, aad_len,
                                                  buf, len, buf, sizeof(buf), &out_len);
        bool encrypt_ok = encrypted == PSA_SUCCESS && out_len == len + 16 && !memcmp(buf, expected, len + 16);
        psa_status_t decrypted = psa_aead_decrypt(id, PSA_ALG_GCM, nonce, sizeof(nonce), aad, aad_len,
                                                  expected, len + 16, expected, sizeof(expected), &out_len);
        psa_destroy_key(id);
        if(!id || !encrypt_ok || decrypted != PSA_SUCCESS || out_len != len || memcmp(expected, plain, len))
            FAIL("vector %zu: encrypt %d, decrypt %d", v, (int)encrypted, (int)decrypted);
    }
    for(int k = 0; k < 2; ++k) {
        size_t key_len = k ? 32 : 16;
        for(size_t i = 0; i < key_len; ++i)
            key[i] = (unsigned char)i;
        for(size_t i = 0; i < sizeof(nonce); ++i)
            nonce[i] = (unsigned char)(0xa0 + i);
        for(size_t i = 0; i < 13; ++i)
            aad[i] = (unsigned char)(0x50 + i);
        psa_key_id_t id = gcm_key(key, key_len);
        CHECK(id != 0);
        for(size_t l = 0; l < sizeof(lengths) / sizeof(lengths[0]); ++l) {
            size_t len = lengths[l], out_len = 0;
            for(size_t i = 0; i < len; ++i)
                buf[i] = (unsigned char)(i * 31 + 7);
            psa_status_t encrypted = psa_aead_encrypt(id, PSA_ALG_GCM, nonce, sizeof(nonce), aad, 13,
                                                      buf, len, buf, sizeof(buf), &out_len);
            char sha[65];
            sha256_hex(buf, out_len, sha);
            psa_status_t decrypted = psa_aead_decrypt(id, PSA_ALG_GCM, nonce, sizeof(nonce), aad, 13,
                                                      buf, len + 16, buf, sizeof(buf), &out_len);
            bool plain_back = decrypted == PSA_SUCCESS && out_len == len;
            for(size_t i = 0; plain_back && i < len; ++i)
                plain_back = buf[i] == (unsigned char)(i * 31 + 7);
            if(encrypted != PSA_SUCCESS || strcmp(sha, digests[k][l]) || !plain_back) {
                psa_destroy_key(id);
                FAIL("AES-%zu length %zu: encrypt %d sha %s, decrypt %d", key_len * 8, len, (int)encrypted, sha,
                     (int)decrypted);
            }
        }
        psa_destroy_key(id);
    }
}

static const struct test tests[] = {
    {"generation_wrap_retires_slot", test_generation_wrap},
    {"abi_mismatch_and_config", test_abi_and_config},
    {"monotonic_clock", test_monotonic},
    {"get_memory", test_get_memory},
    {"https_h2_parallel", test_https_h2},
    {"https1_parallel_not_queued", test_https1_parallel_not_queued},
    {"tls_untrusted", test_tls_failure},
    {"get_file", test_get_file},
    {"non2xx_memory_and_file", test_non2xx},
    {"empty_2xx_file_truncates", test_empty_2xx_file},
    {"cut_body_file_deleted_or_kept", test_cut_body_file},
    {"resume_interrupted_h1", test_resume_h1},
    {"resume_interrupted_h2", test_resume_h2},
    {"resume_restart_complete_mismatch", test_resume_cases},
    {"resume_mismatch_h2", test_resume_h2_mismatch},
    {"abort_file_sink_deletes_or_keeps", test_abort_file},
    {"sink_and_upload_local_io", test_sink_local_io},
    {"submit_size_overflow", test_size_overflow},
    {"fifo_upload_and_sink_local_io", test_fifo},
    {"undeletable_partial_local_io", test_partial_not_deleted},
    {"known_length_body_one_allocation", test_body_allocations},
    {"ca_bundle_not_copied_per_handshake", test_ca_bundle_not_copied},
    {"ca_parsed_once_per_client", test_ca_parsed_once},
    {"disk_full_local_io", test_disk_full},
    {"memory_limit", test_memory_limit},
    {"post_memory_file_empty", test_post},
    {"redirect_body_replay", test_redirect_replay},
    {"see_other_reissued_as_get", test_see_other},
    {"methods_on_the_wire", test_methods},
    {"implicit_headers_suppressed", test_wire_headers},
    {"content_encoding", test_content_encoding},
    {"content_encoding_truncated", test_content_encoding_truncated},
    {"error_body_for_file_sink", test_error_body_file},
    {"send_and_low_speed_limits", test_speed_limits},
    {"invalid_requests", test_invalid_requests},
    {"header_leading_whitespace", test_header_leading_whitespace},
    {"abort_queued_running_finishing", test_abort},
    {"release_before_drain", test_release_state},
    {"capacity", test_capacity},
    {"detached", test_detached},
    {"error_classes", test_errors},
    {"aes_gcm_known_answers", test_aes_gcm_known_answers},
    {"dns_failure", test_dns},
    {"network_faults_recover", test_network_faults},
    {"no_consumer_completes", test_no_consumer},
    {"progress_changes_only", test_progress_partial_drain},
    {"shutdown_inflight_and_waiters", test_shutdown_inflight},
    {"concurrent_shutdown", test_concurrent_shutdown},
    {"destroy", test_destroy},
    {"shutdown_all", test_shutdown_all},
    {"concurrent_submit_abort_drain", test_concurrent},
    {"ws_echo_bearer_ws_wss", test_ws_echo},
    {"tls12_https_and_wss", test_tls12},
    {"ws_handshake_failures", test_ws_handshake_failures},
    {"ws_redirects", test_ws_redirects},
    {"ws_origin_zone", test_ws_origin_zone},
    {"ws_control_frames", test_ws_control_frames},
    {"ws_server_close_answered", test_ws_server_close},
    {"ws_close_reasons_and_invalid_reply", test_ws_close_reasons},
    {"ws_big_frame_and_protocol_errors", test_ws_big_and_protocol_errors},
    {"ws_binary_messages", test_ws_binary},
    {"ws_text_validated_on_send", test_ws_text_validated},
    {"ws_protocol_error_stops_reading", test_ws_protocol_error_stops_reading},
    {"header_lines_validated", test_header_lines_validated},
    {"ws_partial_send_and_capacity", test_ws_partial_send},
    {"ws_slow_consumer_back_pressure", test_ws_slow_consumer},
    {"ws_close_timeout", test_ws_close_timeout},
    {"ws_pong_flood_bounded", test_ws_pong_flood},
    {"ws_empty_send_flood_bounded", test_ws_empty_send_flood},
    {"ws_abort_each_state", test_ws_abort},
    {"ws_invalid_and_states", test_ws_invalid_and_states},
    {"ws_concurrent_send", test_ws_concurrent_send},
};

#if defined(UGO_TEST_WRAP_ONLY)
#  define WRAP_BUILD 1
#else
#  define WRAP_BUILD 0
#endif

int main(int argc, char **argv)
{
    if(argc < 5) {
        fprintf(stderr, "usage: %s http_base https_base ca_pem tmp_dir [test]\n", argv[0]);
        return 2;
    }
    http_base = argv[1];
    https_base = argv[2];
    tmp_dir = argv[4];
    https1_base = getenv("UGO_TEST_HTTPS1");
    tls12_base = getenv("UGO_TEST_TLS12");
    static char dir[1024];
    snprintf(dir, sizeof(dir), "%s", argv[3]);
    char *slash = strrchr(dir, '/');
    if(slash)
        *slash = 0;
    run_dir = dir;
    ca_pem = read_file(argv[3], &ca_len);
    if(!ca_pem) {
        fprintf(stderr, "cannot read %s\n", argv[3]);
        return 2;
    }
    int failed = 0;
    for(size_t i = 0; i < sizeof(tests) / sizeof(tests[0]); ++i) {
        if(argc > 5 && strcmp(argv[5], tests[i].name))
            continue;
        if(WRAP_BUILD != (tests[i].fn == test_generation_wrap))
            continue;
        failure[0] = 0;
        skipped = NULL;
        int64_t start = ugo_monotonic_us();
        tests[i].fn();
        double seconds = (double)(ugo_monotonic_us() - start) / 1e6;
        failed += failure[0] != 0;
        printf("{\"test\":\"%s\",\"pass\":%s,\"seconds\":%.3f", tests[i].name, failure[0] ? "false" : "true", seconds);
        if(skipped)
            printf(",\"skipped\":\"%s\"", skipped);
        if(failure[0]) {
            printf(",\"message\":\"");
            for(const char *p = failure; *p; ++p)
                if(*p == '"' || *p == '\\')
                    printf("\\%c", *p);
                else if((unsigned char)*p >= 0x20)
                    putchar(*p);
            printf("\"");
        }
        printf("}\n");
        fflush(stdout);
    }
    free(ca_pem);
    return failed ? 1 : 0;
}
