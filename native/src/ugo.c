#include "ugo.h"

#include "platform/platform.h"

#include <curl/curl.h>
#include <mbedtls/pem.h>
#include <mbedtls/ssl.h>
#include <mbedtls/x509_crt.h>
#include <psa/crypto.h>

#if defined(MBEDTLS_CIPHER_C) || !defined(MBEDTLS_BLOCK_CIPHER_C)
#error "GCM must use the one-block cipher layer, not the generic cipher layer"
#endif

#include <limits.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>

#include "ca_bundle.h"

#if defined(UGO_TEST_COUNTERS)
unsigned ugo_test_body_allocations;
unsigned ugo_test_ws_partial_sends;
unsigned ugo_test_curl_large_allocations;
unsigned ugo_test_ca_parses;

#define TEST_LARGE_ALLOCATION (64u << 10)

static void count_large(size_t size)
{
    if(size >= TEST_LARGE_ALLOCATION)
        __atomic_fetch_add(&ugo_test_curl_large_allocations, 1u, __ATOMIC_RELAXED);
}

static void *test_malloc(size_t size)
{
    count_large(size);
    return malloc(size);
}

static void *test_realloc(void *p, size_t size)
{
    count_large(size);
    return realloc(p, size);
}

static void *test_calloc(size_t n, size_t size)
{
    count_large(n * size);
    return calloc(n, size);
}

static char *test_strdup(const char *s)
{
    size_t n = strlen(s) + 1;
    char *copy = test_malloc(n);
    return copy ? memcpy(copy, s, n) : NULL;
}
#endif

#ifndef UGO_GENERATION_MAX
#  define UGO_GENERATION_MAX UINT32_MAX
#endif

#if defined(UGO_DEV_TLS)
#  define CLIENT_FLAGS UGO_CLIENT_TLS_INSECURE_DEV
#else
#  define CLIENT_FLAGS 0u
#endif

#define REQUEST_FLAGS (UGO_REQ_KEEP_PARTIAL | UGO_REQ_DETACHED | UGO_REQ_FINISH_IN_BACKGROUND | UGO_REQ_RESUME)
#define POLL_MS 60000
/* curl reports progress after every read; an event per read costs the drainer a wakeup per network chunk. */
#define PROGRESS_INTERVAL_US 10000
#define WS_CONTROL_MAX 125u
#define WS_DROP_BYTES 16384u
/* Counted in curl_ws_recv calls, not bytes: an empty PONG costs a call and no bytes. */
#define WS_RECV_PER_PASS 256
/* The managed HTTP default; a WebSocket request has no limit of its own. */
#define WS_MAX_REDIRECTS 10u

enum { SLOT_FREE, SLOT_ACTIVE, SLOT_DONE, SLOT_FINISHED, SLOT_RETIRED };

struct slot;
struct ws;

struct upload {
    const uint8_t *data;
    uint64_t len;
    uint64_t pos;
    const char *path;
    plat_file file;
    struct slot *slot;
};

struct part {
    const char *name;
    const char *filename;
    const char *type;
    struct upload up;
};

/* Built by submit on the caller's thread, owned by the owner thread from then on. */
struct req {
    CURLU *url;
    struct curl_slist *headers;
    curl_mime *mime;
    const char *custom_method;
    bool head;
    bool decode;
    uint32_t body_kind;
    uint32_t flags;
    uint32_t part_count;
    struct upload body;
    struct part *parts;
    const char *sink_path;
    uint64_t memory_limit;
    uint64_t max_recv;
    uint64_t max_send;
    uint32_t low_speed_bytes;
    uint32_t low_speed_time_s;
    uint32_t connect_timeout_ms;
    uint32_t total_timeout_ms;
    uint32_t max_redirects;
    CURLU *origin;
    struct curl_slist *redirect_headers;
};

struct slot {
    /* Guarded by client->lock. */
    uint32_t state;
    uint32_t generation;
    bool detached;
    bool abort;
    bool owner_queued;
    bool event_queued;
    bool ev_headers;
    bool ev_progress;
    bool ev_completed;
    bool bg_expired;
    void *bg;
    ugo_event headers_event;
    ugo_event progress_event;
    ugo_event completed_event;
    struct req *req;
    struct ws *ws;

    /* Written by the owner while ACTIVE; read by callers once FINISHED. */
    uint8_t *body;
    uint64_t body_len;
    uint64_t body_cap;
    char *hdr;
    size_t hdr_len;
    size_t hdr_cap;
    size_t final_len;
    bool final_seen;
    char error[CURL_ERROR_SIZE];
    /* A WebSocket close reason may hold NUL. */
    size_t error_len;

    /* Owner thread only. */
    CURL *easy;
    bool started;
    bool has_location;
    bool see_other;
    bool sink_opened;
    uint32_t status;
    uint32_t version;
    int64_t content_length;
    plat_file sink;
    uint64_t resume_from;
    uint64_t resume_base;
    uint64_t range_left;
    bool resume_bad;
    bool range_ok;
    bool range_multipart;
    uint32_t range_count;
    int64_t range_first;
    int64_t range_last;
    int64_t range_total;
    uint32_t fail_class;
    uint32_t fail_detail;
    curl_off_t last_rx;
    curl_off_t last_tx;
    curl_off_t rx_total;
    curl_off_t tx_total;
    int64_t progress_us;
    bool progress_held;
    uint32_t prior_redirects;
    int64_t prior_us;
    bool prior_connected;
    uint64_t prior_at;
    struct client *client;
    uint32_t index;
};

struct client {
    plat_mutex lock;
    plat_cond events_cond;
    plat_cond joined_cond;
    plat_cond owner_cond;
    CURLM *multi;
    bool multi_alive;
    bool broken;
    bool shutting_down;
    bool joined;
    bool draining;
    plat_thread thread;
    uint32_t max;
    uint32_t dns_ttl_s;
    uint64_t performs;
    struct slot *slots;
    uint32_t *free_stack;
    uint32_t free_count;
    uint32_t *owner_ring;
    uint32_t owner_head;
    uint32_t owner_count;
    uint32_t *event_ring;
    uint32_t event_head;
    uint32_t event_count;
    uint32_t *scratch;
    struct curl_waitfd *waitfds;
    uint32_t ws_count;
    uint8_t drop[WS_DROP_BYTES];
    bool insecure;

    /* Owner thread only once the client is created. */
    const char *ca_pem;
    size_t ca_len;
    char *ca_copy;
    uint32_t ca_state;
    mbedtls_x509_crt ca;
    struct client *next;
};

enum { CA_UNPARSED, CA_PARSED };
enum { WS_CONNECTING, WS_OPEN, WS_CLOSING, WS_ECHOING, WS_CLOSED };
enum { OUT_NONE, OUT_MESSAGE, OUT_PONG, OUT_CLOSE };

struct ws_msg {
    uint64_t off;
    uint64_t len;
    int64_t time_us;
    uint32_t type;
};

/* One allocation per socket at ws_open: this struct, then the message index, the backlog and the send buffer. */
struct ws {
    uint64_t backlog_cap;
    uint32_t count_cap;
    uint32_t send_cap;
    uint32_t max_message;
    uint32_t close_timeout_ms;
    struct ws_msg *msgs;
    uint8_t *backlog;
    uint8_t *send;
    char accept[29];

    /* Guarded by client->lock. */
    uint32_t state;
    bool rx_paused;
    uint64_t emitted;
    uint64_t drained;
    uint64_t released;
    uint32_t msg_head;
    uint64_t unreleased_bytes;
    uint32_t send_head;
    uint32_t send_used;
    int64_t deadline_us;
    uint8_t close_out[WS_CONTROL_MAX];
    uint32_t close_out_len;

    /* Owner thread only. */
    uint32_t redirects;
    int64_t redirect_us;
    curl_socket_t fd;
    bool hs_upgrade;
    bool hs_connection;
    bool hs_accept;
    bool hs_extensions;
    bool hs_protocol;
    uint32_t hs_accepts;
    uint32_t hs_protocols;
    bool rx_blocked;
    bool rx_more;
    bool want_write;
    bool tx_more;
    bool proto_error;
    bool remote_closed;
    bool close_sent;
    uint32_t first_code;
    uint32_t first_detail;
    uint32_t first_reason_len;
    char first_reason[WS_CONTROL_MAX];
    uint64_t part_off;
    uint64_t part_len;
    uint32_t ctl_len;
    uint8_t ctl[WS_CONTROL_MAX];
    bool pong_pending;
    uint32_t pong_len;
    uint8_t pong[WS_CONTROL_MAX];
    uint32_t out_kind;
    uint32_t out_type;
    uint64_t out_len;
    uint64_t out_sent;
    uint8_t out_ctl[WS_CONTROL_MAX];
};

static plat_mutex live_lock = PLAT_MUTEX_INIT;
static struct client *live;
static bool process_ready;

static void process_init(void)
{
    plat_tls_init();
#if defined(UGO_TEST_COUNTERS)
    if(curl_global_init_mem(CURL_GLOBAL_DEFAULT, test_malloc, free, test_realloc, test_strdup, test_calloc) != CURLE_OK)
        return;
#else
    if(curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK)
        return;
#endif
    /* mbedTLS builds this list lazily on first use; two owners' first handshakes race on it. */
    mbedtls_ssl_list_ciphersuites();
    process_ready = true;
}

int64_t ugo_monotonic_us(void)
{
    return plat_now_us();
}

/* Every CURLcode of the pinned curl. A new curl adds codes: the assert forces a review. */
_Static_assert(CURL_LAST == 102, "review error_class() for the new CURLcode values");

static uint32_t error_class(CURLcode code)
{
    switch(code) {
    case CURLE_OK:
        return UGO_CLASS_NONE;
    case CURLE_COULDNT_RESOLVE_PROXY:
    case CURLE_COULDNT_RESOLVE_HOST:
        return UGO_CLASS_DNS;
    case CURLE_COULDNT_CONNECT:
    case CURLE_INTERFACE_FAILED:
    case CURLE_NO_CONNECTION_AVAILABLE:
    case CURLE_PROXY:
    case CURLE_QUIC_CONNECT_ERROR:
        return UGO_CLASS_CONNECT;
    case CURLE_SSL_CONNECT_ERROR:
    case CURLE_SSL_ENGINE_NOTFOUND:
    case CURLE_SSL_ENGINE_SETFAILED:
    case CURLE_SSL_CERTPROBLEM:
    case CURLE_SSL_CIPHER:
    case CURLE_PEER_FAILED_VERIFICATION:
    case CURLE_USE_SSL_FAILED:
    case CURLE_SSL_ENGINE_INITFAILED:
    case CURLE_SSL_CACERT_BADFILE:
    case CURLE_SSL_SHUTDOWN_FAILED:
    case CURLE_SSL_CRL_BADFILE:
    case CURLE_SSL_ISSUER_ERROR:
    case CURLE_SSL_PINNEDPUBKEYNOTMATCH:
    case CURLE_SSL_INVALIDCERTSTATUS:
    case CURLE_SSL_CLIENTCERT:
    case CURLE_ECH_REQUIRED:
        return UGO_CLASS_TLS;
    case CURLE_OPERATION_TIMEDOUT:
    case CURLE_FTP_ACCEPT_TIMEOUT:
        return UGO_CLASS_TIMEOUT;
    /* submit validated the URL and scheme: these only come from a redirect target. */
    case CURLE_UNSUPPORTED_PROTOCOL:
    case CURLE_URL_MALFORMAT:
    case CURLE_WEIRD_SERVER_REPLY:
    case CURLE_HTTP2:
    case CURLE_PARTIAL_FILE:
    case CURLE_UPLOAD_FAILED:
    case CURLE_RANGE_ERROR:
    case CURLE_BAD_DOWNLOAD_RESUME:
    case CURLE_TOO_MANY_REDIRECTS:
    case CURLE_GOT_NOTHING:
    case CURLE_SEND_ERROR:
    case CURLE_RECV_ERROR:
    case CURLE_BAD_CONTENT_ENCODING:
    case CURLE_FILESIZE_EXCEEDED:
    case CURLE_SEND_FAIL_REWIND:
    case CURLE_AGAIN:
    case CURLE_CHUNK_FAILED:
    case CURLE_HTTP2_STREAM:
    case CURLE_AUTH_ERROR:
    case CURLE_HTTP3:
    case CURLE_TOO_LARGE:
    /* The shim's write callbacks set their own class; a bare WRITE_ERROR comes from curl's content
       decoder (bytes after the end of the gzip stream, a second gzip member). */
    case CURLE_WRITE_ERROR:
        return UGO_CLASS_TRANSFER;
    case CURLE_HTTP_RETURNED_ERROR:
        return UGO_CLASS_HTTP_STATUS;
    case CURLE_READ_ERROR:
    case CURLE_FILE_COULDNT_READ_FILE:
        return UGO_CLASS_LOCAL_IO;
    case CURLE_FAILED_INIT:
    case CURLE_NOT_BUILT_IN:
    case CURLE_REMOTE_ACCESS_DENIED:
    case CURLE_FTP_ACCEPT_FAILED:
    case CURLE_FTP_WEIRD_PASS_REPLY:
    case CURLE_FTP_WEIRD_PASV_REPLY:
    case CURLE_FTP_WEIRD_227_FORMAT:
    case CURLE_FTP_CANT_GET_HOST:
    case CURLE_FTP_COULDNT_SET_TYPE:
    case CURLE_FTP_COULDNT_RETR_FILE:
    case CURLE_OBSOLETE20:
    case CURLE_QUOTE_ERROR:
    case CURLE_OBSOLETE24:
    case CURLE_OUT_OF_MEMORY:
    case CURLE_OBSOLETE29:
    case CURLE_FTP_PORT_FAILED:
    case CURLE_FTP_COULDNT_USE_REST:
    case CURLE_OBSOLETE32:
    case CURLE_OBSOLETE34:
    case CURLE_LDAP_CANNOT_BIND:
    case CURLE_LDAP_SEARCH_FAILED:
    case CURLE_OBSOLETE40:
    case CURLE_OBSOLETE41:
    case CURLE_ABORTED_BY_CALLBACK:
    case CURLE_BAD_FUNCTION_ARGUMENT:
    case CURLE_OBSOLETE44:
    case CURLE_OBSOLETE46:
    case CURLE_UNKNOWN_OPTION:
    case CURLE_SETOPT_OPTION_SYNTAX:
    case CURLE_OBSOLETE50:
    case CURLE_OBSOLETE51:
    case CURLE_OBSOLETE57:
    case CURLE_OBSOLETE62:
    case CURLE_LOGIN_DENIED:
    case CURLE_TFTP_NOTFOUND:
    case CURLE_TFTP_PERM:
    case CURLE_REMOTE_DISK_FULL:
    case CURLE_TFTP_ILLEGAL:
    case CURLE_TFTP_UNKNOWNID:
    case CURLE_REMOTE_FILE_EXISTS:
    case CURLE_TFTP_NOSUCHUSER:
    case CURLE_OBSOLETE75:
    case CURLE_OBSOLETE76:
    case CURLE_REMOTE_FILE_NOT_FOUND:
    case CURLE_SSH:
    case CURLE_FTP_PRET_FAILED:
    case CURLE_RTSP_CSEQ_ERROR:
    case CURLE_RTSP_SESSION_ERROR:
    case CURLE_FTP_BAD_FILE_LIST:
    case CURLE_RECURSIVE_API_CALL:
    case CURLE_UNRECOVERABLE_POLL:
    case CURL_LAST:
        return UGO_CLASS_INTERNAL;
    }
    return UGO_CLASS_INTERNAL;
}

static bool bytes_ok(ugo_bytes b)
{
    return (b.ptr || !b.len) && b.len <= SIZE_MAX / 2;
}

static const void *bytes_ptr(ugo_bytes b)
{
    return (const void *)(uintptr_t)b.ptr;
}

static bool has_nul(ugo_bytes b)
{
    return b.len && memchr(bytes_ptr(b), 0, (size_t)b.len);
}

static long to_long(uint64_t v)
{
    return v > LONG_MAX ? LONG_MAX : (long)v;
}

static struct slot *lookup(struct client *c, uint64_t id)
{
    uint32_t index = (uint32_t)id;
    uint32_t generation = (uint32_t)(id >> 32);
    if(index >= c->max)
        return NULL;
    struct slot *s = &c->slots[index];
    if(s->generation != generation || s->state == SLOT_FREE || s->state == SLOT_RETIRED)
        return NULL;
    return s;
}

static void wake_owner(struct client *c)
{
    if(c->multi_alive)
        curl_multi_wakeup(c->multi);
    plat_cond_signal(&c->owner_cond);
}

static void queue_owner(struct client *c, struct slot *s)
{
    if(s->owner_queued)
        return;
    s->owner_queued = true;
    c->owner_ring[(c->owner_head + c->owner_count++) % c->max] = s->index;
    wake_owner(c);
}

static void queue_event(struct client *c, struct slot *s)
{
    if(s->event_queued)
        return;
    s->event_queued = true;
    c->event_ring[(c->event_head + c->event_count++) % c->max] = s->index;
    plat_cond_signal(&c->events_cond);
}

static void free_slot(struct client *c, struct slot *s)
{
    s->state = s->generation >= UGO_GENERATION_MAX ? SLOT_RETIRED : SLOT_FREE;
    if(s->state == SLOT_FREE) {
        s->generation++;
        c->free_stack[c->free_count++] = s->index;
    }
}

static void set_message(struct slot *s, const char *message)
{
    size_t n = strlen(message);
    if(n >= sizeof(s->error))
        n = sizeof(s->error) - 1;
    memcpy(s->error, message, n);
    s->error[n] = 0;
}

static void set_error(struct slot *s, uint32_t cls, uint32_t detail, const char *message)
{
    if(s->fail_class)
        return;
    s->fail_class = cls;
    s->fail_detail = detail;
    set_message(s, message);
}

static void free_req(struct req *r)
{
    if(!r)
        return;
    curl_url_cleanup(r->url);
    curl_url_cleanup(r->origin);
    curl_slist_free_all(r->headers);
    curl_slist_free_all(r->redirect_headers);
    curl_mime_free(r->mime);
    if(r->body.file != PLAT_NO_FILE)
        plat_file_close(r->body.file);
    for(uint32_t i = 0; i < r->part_count; ++i)
        if(r->parts[i].up.file != PLAT_NO_FILE)
            plat_file_close(r->parts[i].up.file);
    free(r);
}

static bool reserve(void **buffer, size_t *cap, size_t need, size_t initial)
{
    if(need <= *cap)
        return true;
    size_t next = *cap ? *cap : initial;
    while(next < need)
        next = next > SIZE_MAX / 2 ? need : next * 2;
    void *p = realloc(*buffer, next);
    if(!p)
        return false;
    *buffer = p;
    *cap = next;
    return true;
}

static bool line_is(const char *line, size_t len, const char *name, const char **value, size_t *value_len)
{
    size_t n = strlen(name);
    if(len < n)
        return false;
    for(size_t i = 0; i < n; ++i) {
        char ch = line[i];
        if(ch >= 'A' && ch <= 'Z')
            ch = (char)(ch - 'A' + 'a');
        if(ch != name[i])
            return false;
    }
    const char *v = line + n;
    const char *end = line + len;
    while(v < end && (*v == ' ' || *v == '\t'))
        ++v;
    while(end > v && (end[-1] == '\r' || end[-1] == '\n' || end[-1] == ' ' || end[-1] == '\t'))
        --end;
    *value = v;
    *value_len = (size_t)(end - v);
    return true;
}

static bool has_token(const char *value, size_t len, const char *token, size_t n, bool fold)
{
    const char *end = value + len;
    while(value < end) {
        while(value < end && (*value == ' ' || *value == '\t' || *value == ','))
            ++value;
        const char *start = value;
        while(value < end && *value != ',')
            ++value;
        const char *stop = value;
        while(stop > start && (stop[-1] == ' ' || stop[-1] == '\t'))
            --stop;
        if((size_t)(stop - start) == n && (fold ? curl_strnequal(start, token, n) : !memcmp(start, token, n)))
            return true;
    }
    return false;
}

static bool header_named(const char *line, const char *name);

/* Subprotocol values compare case-sensitively (RFC 6455 §4.1), unlike the header tokens around them. */
static bool protocol_offered(const struct curl_slist *list, const char *value, size_t len)
{
    for(; len && list; list = list->next) {
        const char *colon = strchr(list->data, ':');
        if(colon && header_named(list->data, "Sec-WebSocket-Protocol") &&
           has_token(colon + 1, strlen(colon + 1), value, len, false))
            return true;
    }
    return false;
}

static void parse_status_line(struct slot *s, const char *line, size_t len)
{
    s->status = 0;
    s->version = UGO_HTTP_NONE;
    if(len >= 8 && line[5] == '1' && line[6] == '.')
        s->version = line[7] == '0' ? UGO_HTTP_1_0 : UGO_HTTP_1_1;
    else if(len >= 6 && line[5] == '2')
        s->version = UGO_HTTP_2;
    const char *p = memchr(line, ' ', len);
    if(!p)
        return;
    ++p;
    uint32_t status = 0;
    for(int i = 0; i < 3 && p < line + len && *p >= '0' && *p <= '9'; ++i, ++p)
        status = status * 10 + (uint32_t)(*p - '0');
    s->status = status;
}

static void emit_headers(struct client *c, struct slot *s)
{
    plat_mutex_lock(&c->lock);
    ugo_event *e = &s->headers_event;
    e->kind = UGO_EV_HEADERS;
    e->id = (uint64_t)s->generation << 32 | s->index;
    e->time_us = plat_now_us();
    e->u.headers.http_status = s->status;
    e->u.headers.http_version = s->version;
    e->u.headers.content_length = s->content_length;
    e->u.headers.headers.ptr = (uint64_t)(uintptr_t)s->hdr;
    e->u.headers.headers.len = s->final_len;
    e->u.headers.resume_offset = s->resume_base;
    s->ev_headers = true;
    queue_event(c, s);
    plat_mutex_unlock(&c->lock);
}

static int64_t parse_digits(const char **p, const char *end)
{
    int64_t n = 0;
    const char *start = *p;
    for(; *p < end && **p >= '0' && **p <= '9'; ++*p) {
        if(n > (INT64_MAX - 9) / 10)
            return -1;
        n = n * 10 + (**p - '0');
    }
    return *p == start ? -1 : n;
}

static bool parse_content_range(struct slot *s, const char *v, size_t n)
{
    const char *end = v + n;
    if(n < 6 || !curl_strnequal(v, "bytes ", 6))
        return false;
    const char *p = v + 6;
    int64_t first = -1, last = -1, total = -1;
    if(p < end && *p == '*') {
        ++p;
    } else {
        first = parse_digits(&p, end);
        if(first < 0 || p >= end || *p++ != '-')
            return false;
        last = parse_digits(&p, end);
        if(last < first)
            return false;
    }
    if(p >= end || *p++ != '/')
        return false;
    if(p < end && *p == '*')
        ++p;
    else if((total = parse_digits(&p, end)) < 0)
        return false;
    if(p != end || (first < 0 && total < 0) || (total >= 0 && last >= total))
        return false;
    s->range_first = first;
    s->range_last = last;
    s->range_total = total;
    return true;
}

/* Deletes the file (unless KEEP_PARTIAL) only when the server contradicts it; a server that is merely
 * inconsistent with itself may still be right about the bytes already on disk. */
static bool resume_mismatch(struct slot *s, bool contradicts_file, const char *message)
{
    s->resume_bad = contradicts_file;
    set_error(s, UGO_CLASS_TRANSFER, UGO_DETAIL_RESUME_MISMATCH, message);
    return false;
}

static bool resume_check(struct slot *s)
{
    int64_t have = (int64_t)s->resume_from;
    bool one = s->range_count == 1 && s->range_ok;
    if(s->status == 206) {
        if(!one || s->range_first < 0 || s->range_multipart)
            return resume_mismatch(s, false, "the 206 carries no single byte range");
        if(s->range_first != have)
            return resume_mismatch(s, true, "the 206 does not start at the end of the partial file");
        uint64_t len = (uint64_t)(s->range_last - s->range_first) + 1;
        if(s->content_length >= 0 && (uint64_t)s->content_length != len)
            return resume_mismatch(s, false, "the 206 Content-Length differs from its Content-Range");
        s->resume_base = s->resume_from;
        s->range_left = len;
    } else if(s->status == 416) {
        if(!one || s->range_first >= 0)
            return resume_mismatch(s, false, "the 416 carries no resource length");
        if(s->range_total != have)
            return resume_mismatch(s, s->range_total < have, "the 416 resource length differs from the partial file");
        s->resume_base = s->resume_from;
        return true;
    } else if(s->status < 200 || s->status > 299) {
        return true;
    }
    if(plat_file_truncate(s->sink, s->resume_base)) {
        set_error(s, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "cannot position the sink file");
        return false;
    }
    return true;
}

static size_t on_header(char *data, size_t size, size_t count, void *user)
{
    struct slot *s = user;
    size_t len = size * count;
    if(s->final_seen)
        return len;
    struct ws *w = s->ws;
    if(len >= 5 && !memcmp(data, "HTTP/", 5)) {
        s->hdr_len = 0;
        s->has_location = false;
        s->content_length = -1;
        s->range_count = 0;
        s->range_multipart = false;
        parse_status_line(s, data, len);
        if(w) {
            w->hs_upgrade = w->hs_connection = w->hs_accept = w->hs_extensions = w->hs_protocol = false;
            w->hs_accepts = w->hs_protocols = 0;
        }
    }
    bool blank = (len == 2 && data[0] == '\r' && data[1] == '\n') || (len == 1 && data[0] == '\n');
    const char *value;
    size_t value_len;
    if(s->detached) {
        if(line_is(data, len, "location:", &value, &value_len))
            s->has_location = s->has_location || value_len > 0;
        if(blank)
            s->see_other = s->status == 303 && s->has_location && s->req->max_redirects;
        return len;
    }
    if(!reserve((void **)&s->hdr, &s->hdr_cap, s->hdr_len + len, 1024)) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, "out of memory for response headers");
        return CURL_WRITEFUNC_ERROR;
    }
    memcpy(s->hdr + s->hdr_len, data, len);
    s->hdr_len += len;
    if(line_is(data, len, "location:", &value, &value_len))
        s->has_location = s->has_location || value_len > 0;
    else if(line_is(data, len, "content-length:", &value, &value_len)) {
        int64_t n = 0;
        size_t i = 0;
        for(; i < value_len && value[i] >= '0' && value[i] <= '9' && n <= (INT64_MAX - (value[i] - '0')) / 10; ++i)
            n = n * 10 + (value[i] - '0');
        s->content_length = i && i == value_len ? n : -1;
    } else if(s->resume_from && line_is(data, len, "content-range:", &value, &value_len)) {
        if(!s->range_count++)
            s->range_ok = parse_content_range(s, value, value_len);
    } else if(s->resume_from && line_is(data, len, "content-type:", &value, &value_len)) {
        s->range_multipart = s->range_multipart || (value_len >= 20 && curl_strnequal(value, "multipart/byteranges", 20));
    } else if(w && s->status == 101) {
        if(line_is(data, len, "upgrade:", &value, &value_len))
            w->hs_upgrade = value_len == 9 && curl_strnequal(value, "websocket", 9);
        else if(line_is(data, len, "connection:", &value, &value_len))
            w->hs_connection = w->hs_connection || has_token(value, value_len, "upgrade", 7, true);
        else if(line_is(data, len, "sec-websocket-accept:", &value, &value_len))
            w->hs_accept = ++w->hs_accepts == 1 && value_len == 28 && !memcmp(value, w->accept, 28);
        else if(line_is(data, len, "sec-websocket-extensions:", &value, &value_len))
            w->hs_extensions = true;
        else if(line_is(data, len, "sec-websocket-protocol:", &value, &value_len))
            w->hs_protocol = ++w->hs_protocols == 1 && protocol_offered(s->req->headers, value, value_len);
    }
    if(blank && w && s->status == 101) {
        s->final_seen = true;
        s->final_len = s->hdr_len;
        return len;
    }
    bool redirect = s->status >= 300 && s->status < 400 && s->has_location && (s->req->max_redirects || w);
    if(blank)
        s->see_other = redirect && s->status == 303 && !w;
    if(blank && s->status >= 200 && !redirect) {
        s->final_seen = true;
        s->final_len = s->hdr_len;
        bool ok = !s->resume_from || resume_check(s);
        emit_headers(s->client, s);
        if(!ok)
            return CURL_WRITEFUNC_ERROR;
    }
    return len;
}

/* The only hook between curl following a 303 and sending its next request, after curl resolved the target and counted
   the hop. */
static int on_prereq(void *user, char *remote_ip, char *local_ip, int remote_port, int local_port)
{
    (void)remote_ip;
    (void)local_ip;
    (void)remote_port;
    (void)local_port;
    struct slot *s = user;
    return s->see_other ? CURL_PREREQFUNC_ABORT : CURL_PREREQFUNC_OK;
}

static size_t on_write(char *data, size_t size, size_t count, void *user)
{
    struct slot *s = user;
    struct req *r = s->req;
    size_t len = size * count;
    if(s->detached || s->ws)
        return len;
    bool ok = s->status >= 200 && s->status <= 299;
    if(r->sink_path && ok) {
        if(!s->sink_opened) {
            s->sink = plat_file_create(r->sink_path);
            if(s->sink == PLAT_NO_FILE) {
                set_error(s, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "cannot create the sink file");
                return CURL_WRITEFUNC_ERROR;
            }
            s->sink_opened = true;
        }
        size_t take = len;
        if(s->resume_base && s->status == 206) {
            if(take > s->range_left)
                take = (size_t)s->range_left;
            s->range_left -= take;
        }
        if(plat_file_write(s->sink, data, take)) {
            set_error(s, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "write to the sink file failed");
            return CURL_WRITEFUNC_ERROR;
        }
        if(take < len) {
            set_error(s, UGO_CLASS_TRANSFER, UGO_DETAIL_RESUME_MISMATCH, "the 206 body is longer than its Content-Range");
            return CURL_WRITEFUNC_ERROR;
        }
        return len;
    }
    uint64_t limit = r->sink_path ? UGO_ERROR_BODY_MAX : r->memory_limit;
    size_t take = len > limit - s->body_len ? (size_t)(limit - s->body_len) : len;
    uint64_t need = s->body_len + take;
    if(need > s->body_cap) {
        uint64_t next = s->body_cap ? s->body_cap * 2 : 16384;
        if(!s->body && s->content_length > 0)
            next = (uint64_t)s->content_length;
        if(next > limit)
            next = limit;
        if(next < need)
            next = need;
        uint8_t *grown = next <= SIZE_MAX ? realloc(s->body, (size_t)next) : NULL;
#if defined(UGO_TEST_COUNTERS)
        __atomic_fetch_add(&ugo_test_body_allocations, 1u, __ATOMIC_RELAXED);
#endif
        if(!grown) {
            set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, "out of memory for the response body");
            return CURL_WRITEFUNC_ERROR;
        }
        s->body = grown;
        s->body_cap = next;
    }
    if(take)
        memcpy(s->body + s->body_len, data, take);
    s->body_len += take;
    if(take < len) {
        set_error(s, ok ? UGO_CLASS_TRANSFER : UGO_CLASS_HTTP_STATUS, UGO_DETAIL_MEMORY_LIMIT,
                  "response body exceeds its memory limit");
        return CURL_WRITEFUNC_ERROR;
    }
    return len;
}

static void publish_progress(struct client *c, struct slot *s, int64_t now)
{
    bool appending = s->resume_base && s->status == 206;
    uint64_t base = appending ? s->resume_base : 0;
    ugo_event *e = &s->progress_event;
    e->kind = UGO_EV_PROGRESS;
    e->id = (uint64_t)s->generation << 32 | s->index;
    e->time_us = now;
    e->u.progress.received = base + (uint64_t)s->last_rx;
    e->u.progress.receive_total = appending ? (s->range_total >= 0 ? s->range_total : s->range_last + 1)
                                            : s->rx_total > 0 ? s->rx_total : -1;
    e->u.progress.sent = (uint64_t)s->last_tx;
    e->u.progress.send_total = s->tx_total > 0 ? s->tx_total : -1;
    s->ev_progress = true;
    s->progress_held = false;
    queue_event(c, s);
}

static int on_progress(void *user, curl_off_t dltotal, curl_off_t dlnow, curl_off_t ultotal, curl_off_t ulnow)
{
    struct slot *s = user;
    if(dlnow != s->last_rx || ulnow != s->last_tx) {
        s->last_rx = dlnow;
        s->last_tx = ulnow;
        s->rx_total = dltotal;
        s->tx_total = ultotal;
    } else if(!s->progress_held)
        return 0;
    int64_t now = plat_now_us();
    if(now - s->progress_us < PROGRESS_INTERVAL_US) {
        s->progress_held = true;
        return 0;
    }
    s->progress_us = now;
    struct client *c = s->client;
    plat_mutex_lock(&c->lock);
    publish_progress(c, s, now);
    plat_mutex_unlock(&c->lock);
    return 0;
}

static size_t on_read(char *buffer, size_t size, size_t count, void *user)
{
    struct upload *u = user;
    size_t len = size * count;
    if(u->file != PLAT_NO_FILE) {
        int64_t n = plat_file_read(u->file, buffer, len);
        if(n < 0) {
            set_error(u->slot, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "read from the upload file failed");
            return CURL_READFUNC_ABORT;
        }
        u->pos += (uint64_t)n;
        return (size_t)n;
    }
    uint64_t left = u->len - u->pos;
    if(len > left)
        len = (size_t)left;
    if(len)
        memcpy(buffer, u->data + u->pos, len);
    u->pos += len;
    return len;
}

static int on_seek(void *user, curl_off_t offset, int origin)
{
    struct upload *u = user;
    if(origin != SEEK_SET || offset < 0)
        return CURL_SEEKFUNC_CANTSEEK;
    if(u->file != PLAT_NO_FILE && plat_file_seek(u->file, (uint64_t)offset)) {
        set_error(u->slot, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "seek in the upload file failed");
        return CURL_SEEKFUNC_FAIL;
    }
    u->pos = (uint64_t)offset;
    return CURL_SEEKFUNC_OK;
}

static int64_t open_upload(struct slot *s, struct upload *u)
{
    u->slot = s;
    if(!u->path)
        return (int64_t)u->len;
    u->file = plat_file_open_read(u->path);
    int64_t size = u->file == PLAT_NO_FILE ? -1 : plat_file_size(u->file);
    if(size < 0)
        set_error(s, UGO_CLASS_LOCAL_IO, UGO_DETAIL_NONE, "cannot open the upload file");
    return size;
}

static void fill_completed(struct slot *s, uint32_t cls, uint32_t detail, CURLcode code)
{
    ugo_event *e = &s->completed_event;
    memset(e, 0, sizeof(*e));
    e->kind = UGO_EV_COMPLETED;
    e->id = (uint64_t)s->generation << 32 | s->index;
    ugo_ev_completed *d = &e->u.completed;
    d->error_class = cls;
    d->detail = detail;
    d->curl_code = (int32_t)code;
    if(s->ws)
        d->ws_close_code = s->ws->first_code ? s->ws->first_code : 1006;
    if(!s->started)
        return;
    CURL *h = s->easy;
    long v = 0;
    curl_off_t t = 0;
    if(curl_easy_getinfo(h, CURLINFO_RESPONSE_CODE, &v) == CURLE_OK)
        d->http_status = (uint32_t)v;
    if(curl_easy_getinfo(h, CURLINFO_HTTP_VERSION, &v) == CURLE_OK)
        d->http_version = v == CURL_HTTP_VERSION_1_0 ? UGO_HTTP_1_0 :
                          v == CURL_HTTP_VERSION_1_1 ? UGO_HTTP_1_1 :
                          v == CURL_HTTP_VERSION_2_0 ? UGO_HTTP_2 : UGO_HTTP_NONE;
    if(curl_easy_getinfo(h, CURLINFO_SIZE_DOWNLOAD_T, &t) == CURLE_OK)
        d->bytes_received = (uint64_t)t;
    if(curl_easy_getinfo(h, CURLINFO_SIZE_UPLOAD_T, &t) == CURLE_OK)
        d->bytes_sent = (uint64_t)t;
    long connects = 0;
    curl_off_t conn_id = -1;
    if(curl_easy_getinfo(h, CURLINFO_NUM_CONNECTS, &connects) == CURLE_OK &&
       curl_easy_getinfo(h, CURLINFO_CONN_ID, &conn_id) == CURLE_OK)
        d->connection_reused = connects == 0 && conn_id >= 0 && !s->prior_connected;
    if(curl_easy_getinfo(h, CURLINFO_REDIRECT_COUNT, &v) == CURLE_OK)
        d->redirect_count = (uint32_t)v;
    static const CURLINFO phases[] = {
        CURLINFO_QUEUE_TIME_T, CURLINFO_NAMELOOKUP_TIME_T, CURLINFO_CONNECT_TIME_T,
        CURLINFO_APPCONNECT_TIME_T, CURLINFO_STARTTRANSFER_TIME_T, CURLINFO_TOTAL_TIME_T,
        CURLINFO_REDIRECT_TIME_T};
    int64_t *out[] = {&d->queue_us, &d->namelookup_us, &d->connect_us, &d->appconnect_us,
                      &d->starttransfer_us, &d->total_us, &d->redirect_us};
    for(size_t i = 0; i < sizeof(phases) / sizeof(phases[0]); ++i) {
        t = 0;
        curl_easy_getinfo(h, phases[i], &t);
        *out[i] = t;
    }
    /* Until the next perform runs pretransfer on the re-added handle, CURLINFO still holds the hop already in prior_*. */
    if(s->prior_redirects != 0 && s->prior_at == s->client->performs) {
        d->redirect_count = 0;
        d->total_us = 0;
        d->redirect_us = 0;
    }
    d->redirect_count += s->prior_redirects;
    d->total_us += s->prior_us;
    d->redirect_us += s->prior_us;
    if(s->ws) {
        d->redirect_count = s->ws->redirects;
        d->redirect_us = s->ws->redirect_us;
    }
}

/* The one terminal path of a transfer; owner thread only, never inside a curl callback. */
static void finish(struct client *c, struct slot *s, uint32_t cls, uint32_t detail, CURLcode code)
{
    struct req *r = s->req;
    if(s->started)
        curl_multi_remove_handle(c->multi, s->easy);
    plat_mutex_lock(&c->lock);
    bool aborted = s->abort;
    bool expired = s->bg_expired;
    void *bg = s->bg;
    s->bg = NULL;
    plat_mutex_unlock(&c->lock);
    if(cls != UGO_CLASS_ABORTED && (aborted || expired)) {
        cls = UGO_CLASS_ABORTED;
        detail = aborted ? UGO_DETAIL_CALLER : UGO_DETAIL_BACKGROUND_EXPIRED;
        code = CURLE_OK;
    }
    if(cls == UGO_CLASS_ABORTED)
        set_message(s, detail == UGO_DETAIL_SHUTDOWN ? "client shut down"
                       : detail == UGO_DETAIL_BACKGROUND_EXPIRED ? "background task expired" : "aborted");
    if(r->sink_path && !s->detached) {
        if(cls == UGO_CLASS_NONE && !s->sink_opened) {
            s->sink = plat_file_create(r->sink_path);
            s->sink_opened = s->sink != PLAT_NO_FILE;
            if(!s->sink_opened) {
                cls = UGO_CLASS_LOCAL_IO;
                set_error(s, cls, UGO_DETAIL_NONE, "cannot create the sink file");
            }
        }
        if(s->sink_opened) {
            if(plat_file_close(s->sink) && cls == UGO_CLASS_NONE) {
                cls = UGO_CLASS_LOCAL_IO;
                set_error(s, cls, UGO_DETAIL_NONE, "closing the sink file failed");
            }
            s->sink = PLAT_NO_FILE;
            bool keep = cls == UGO_CLASS_NONE || (r->flags & UGO_REQ_KEEP_PARTIAL) ||
                        ((r->flags & UGO_REQ_RESUME) && !s->resume_bad);
            if(!keep && plat_file_remove(r->sink_path)) {
                cls = UGO_CLASS_LOCAL_IO;
                detail = UGO_DETAIL_NONE;
                set_message(s, "the partial sink file could not be deleted");
            }
        }
    }
    if(cls == UGO_CLASS_NONE) {
        s->error[0] = 0;
    } else if(cls == UGO_CLASS_HTTP_STATUS) {
        char text[] = "HTTP status 000";
        uint32_t st = s->status % 1000;
        text[12] = (char)('0' + st / 100);
        text[13] = (char)('0' + st / 10 % 10);
        text[14] = (char)('0' + st % 10);
        set_message(s, text);
    } else if(!s->error[0]) {
        set_message(s, curl_easy_strerror(code));
    }
    s->error_len = strlen(s->error);
    struct ws *w = s->ws;
    if(w) {
        if(s->started)
            c->ws_count--;
        /* curl fails a refused upgrade before passing the blank line that ends its headers. */
        if(cls == UGO_CLASS_HTTP_STATUS && !s->final_seen && s->hdr_len) {
            s->final_seen = true;
            s->final_len = s->hdr_len;
            emit_headers(c, s);
        }
        if(w->first_code && (cls == UGO_CLASS_NONE || cls == UGO_CLASS_TRANSFER || cls == UGO_CLASS_TIMEOUT)) {
            detail = w->first_detail;
            memcpy(s->error, w->first_reason, w->first_reason_len);
            s->error_len = w->first_reason_len;
        }
    }
    if(!s->detached)
        fill_completed(s, cls, detail, code);
    if(s->easy)
        curl_easy_reset(s->easy);
    s->started = false;
    free_req(r);

    plat_mutex_lock(&c->lock);
    s->req = NULL;
    if(w)
        w->state = WS_CLOSED;
    if(s->detached) {
        free_slot(c, s);
    } else {
        s->completed_event.time_us = plat_now_us();
        if(s->progress_held)
            publish_progress(c, s, s->completed_event.time_us);
        s->state = SLOT_DONE;
        s->ev_completed = true;
        queue_event(c, s);
    }
    plat_mutex_unlock(&c->lock);
    plat_bg_end(bg);
}

static void finish_classified(struct client *c, struct slot *s, CURLcode code)
{
    uint32_t cls, detail = UGO_DETAIL_NONE;
    if(s->fail_class) {
        cls = s->fail_class;
        detail = s->fail_detail;
    } else if(code == CURLE_OK) {
        long status = 0;
        curl_easy_getinfo(s->easy, CURLINFO_RESPONSE_CODE, &status);
        bool complete = s->resume_base && status == 416;
        cls = (status >= 200 && status <= 299) || complete ? UGO_CLASS_NONE : UGO_CLASS_HTTP_STATUS;
        if(cls == UGO_CLASS_NONE && s->resume_base && status == 206) {
            if(s->range_left) {
                cls = UGO_CLASS_TRANSFER;
                detail = UGO_DETAIL_RESUME_MISMATCH;
                set_message(s, "the 206 body is shorter than its Content-Range");
            } else if(s->range_total >= 0 && s->range_last + 1 < s->range_total) {
                cls = UGO_CLASS_TRANSFER;
                set_message(s, "the 206 ended before the end of the resource");
            }
        }
    } else {
        cls = error_class(code);
        if(code == CURLE_SSL_CERTPROBLEM)
            set_message(s, "the CA bundle has a certificate mbedTLS cannot parse");
    }
    finish(c, s, cls, detail, code);
}

static CURLcode resume_open(struct slot *s)
{
    struct req *r = s->req;
    plat_file f = plat_file_open_write(r->sink_path);
    int64_t size = f == PLAT_NO_FILE ? 0 : plat_file_size(f);
    if(size <= 0) {
        if(f != PLAT_NO_FILE)
            plat_file_close(f);
        return CURLE_OK;
    }
    s->sink = f;
    s->sink_opened = true;
    s->resume_from = (uint64_t)size;
    char line[48] = "Range: bytes=";
    char digits[20];
    size_t n = 0, at = 13;
    for(uint64_t v = (uint64_t)size; v; v /= 10)
        digits[n++] = (char)('0' + v % 10);
    while(n)
        line[at++] = digits[--n];
    line[at++] = '-';
    line[at] = 0;
    struct curl_slist *grown = curl_slist_append(r->headers, line);
    if(!grown)
        return CURLE_OUT_OF_MEMORY;
    r->headers = grown;
    return CURLE_OK;
}

static CURLcode on_ssl_ctx(CURL *easy, void *ssl_config, void *userp)
{
    (void)easy;
    struct client *c = userp;
    if(c->ca_state == CA_UNPARSED) {
#if defined(UGO_TEST_COUNTERS)
        __atomic_fetch_add(&ugo_test_ca_parses, 1u, __ATOMIC_RELAXED);
#endif
        /* A positive result counts certificates mbedTLS skipped; trusting the rest would drop roots unnoticed. */
        int rc = mbedtls_x509_crt_parse(&c->ca, (const unsigned char *)c->ca_pem, c->ca_len);
        if(rc) {
            mbedtls_x509_crt_free(&c->ca);
            return rc == MBEDTLS_ERR_X509_ALLOC_FAILED || rc == MBEDTLS_ERR_PEM_ALLOC_FAILED
                ? CURLE_OUT_OF_MEMORY : CURLE_SSL_CERTPROBLEM;
        }
        c->ca_state = CA_PARSED;
        free(c->ca_copy);
        c->ca_copy = NULL;
        c->ca_pem = NULL;
    }
    mbedtls_ssl_conf_ca_chain(ssl_config, &c->ca, NULL);
    return CURLE_OK;
}

static void start(struct client *c, struct slot *s)
{
    struct req *r = s->req;
    if(!s->easy)
        s->easy = curl_easy_init();
    if(!s->easy) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, "curl_easy_init failed");
        finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, CURLE_OUT_OF_MEMORY);
        return;
    }
    CURL *e = s->easy;
    CURLcode rc = CURLE_OK;
    if(r->flags & UGO_REQ_RESUME)
        rc = resume_open(s);
#define SET(option, value) do { if(rc == CURLE_OK) rc = curl_easy_setopt(e, option, value); } while(0)
    SET(CURLOPT_PRIVATE, s);
    SET(CURLOPT_CURLU, r->url);
    SET(CURLOPT_PROTOCOLS_STR, s->ws ? "ws,wss" : "http,https");
    SET(CURLOPT_REDIR_PROTOCOLS_STR, "http,https");
    if(s->ws) {
        SET(CURLOPT_CONNECT_ONLY, 2L);
        SET(CURLOPT_WS_OPTIONS, (long)CURLWS_NOAUTOPONG);
    }
    SET(CURLOPT_NOSIGNAL, 1L);
    SET(CURLOPT_ERRORBUFFER, s->error);
    if(c->insecure) {
        SET(CURLOPT_SSL_VERIFYPEER, 0L);
        SET(CURLOPT_SSL_VERIFYHOST, 0L);
    } else {
        SET(CURLOPT_SSL_CTX_FUNCTION, on_ssl_ctx);
        SET(CURLOPT_SSL_CTX_DATA, c);
    }
    SET(CURLOPT_DNS_CACHE_TIMEOUT, to_long(c->dns_ttl_s));
    SET(CURLOPT_CONNECTTIMEOUT_MS, to_long(r->connect_timeout_ms));
    SET(CURLOPT_TIMEOUT_MS, to_long(r->total_timeout_ms));
    SET(CURLOPT_FOLLOWLOCATION, r->max_redirects ? 1L : 0L);
    SET(CURLOPT_MAXREDIRS, to_long(r->max_redirects));
    SET(CURLOPT_POSTREDIR, (long)CURL_REDIR_POST_ALL);
    SET(CURLOPT_MAX_RECV_SPEED_LARGE, (curl_off_t)(r->max_recv > INT64_MAX ? INT64_MAX : r->max_recv));
    SET(CURLOPT_MAX_SEND_SPEED_LARGE, (curl_off_t)(r->max_send > INT64_MAX ? INT64_MAX : r->max_send));
    SET(CURLOPT_LOW_SPEED_LIMIT, to_long(r->low_speed_bytes));
    SET(CURLOPT_LOW_SPEED_TIME, to_long(r->low_speed_time_s));
    SET(CURLOPT_HTTPHEADER, r->headers);
    SET(CURLOPT_HEADERFUNCTION, on_header);
    SET(CURLOPT_HEADERDATA, s);
    SET(CURLOPT_WRITEFUNCTION, on_write);
    SET(CURLOPT_WRITEDATA, s);
    if(!s->ws) {
        SET(CURLOPT_PREREQFUNCTION, on_prereq);
        SET(CURLOPT_PREREQDATA, s);
    }
    if(!s->detached && !s->ws) {
        SET(CURLOPT_NOPROGRESS, 0L);
        SET(CURLOPT_XFERINFOFUNCTION, on_progress);
        SET(CURLOPT_XFERINFODATA, s);
    }
    if(r->decode)
        SET(CURLOPT_ACCEPT_ENCODING, "gzip");
    if(r->head)
        SET(CURLOPT_NOBODY, 1L);
    if(r->custom_method)
        SET(CURLOPT_CUSTOMREQUEST, r->custom_method);
    if(r->body_kind != UGO_BODY_NONE) {
        if(r->body_kind == UGO_BODY_MULTIPART) {
            r->mime = curl_mime_init(e);
            if(!r->mime)
                rc = CURLE_OUT_OF_MEMORY;
            for(uint32_t i = 0; rc == CURLE_OK && i < r->part_count; ++i) {
                struct part *p = &r->parts[i];
                int64_t size = open_upload(s, &p->up);
                if(size < 0)
                    break;
                curl_mimepart *m = curl_mime_addpart(r->mime);
                if(!m) {
                    rc = CURLE_OUT_OF_MEMORY;
                    break;
                }
                rc = curl_mime_name(m, p->name);
                if(rc == CURLE_OK && p->filename)
                    rc = curl_mime_filename(m, p->filename);
                if(rc == CURLE_OK && p->type)
                    rc = curl_mime_type(m, p->type);
                if(rc == CURLE_OK)
                    rc = curl_mime_data_cb(m, size, on_read, on_seek, NULL, &p->up);
            }
            SET(CURLOPT_MIMEPOST, r->mime);
        } else if(r->body_kind == UGO_BODY_FILE) {
            int64_t size = open_upload(s, &r->body);
            if(size >= 0) {
                SET(CURLOPT_POST, 1L);
                SET(CURLOPT_READFUNCTION, on_read);
                SET(CURLOPT_READDATA, &r->body);
                SET(CURLOPT_SEEKFUNCTION, on_seek);
                SET(CURLOPT_SEEKDATA, &r->body);
                SET(CURLOPT_POSTFIELDSIZE_LARGE, (curl_off_t)size);
            }
        } else {
            SET(CURLOPT_POSTFIELDSIZE_LARGE, (curl_off_t)r->body.len);
            SET(CURLOPT_POSTFIELDS, r->body.len ? (const char *)r->body.data : "");
        }
    }
#undef SET
    if(s->fail_class) {
        finish(c, s, s->fail_class, s->fail_detail, CURLE_OK);
        return;
    }
    if(rc == CURLE_OK && curl_multi_add_handle(c->multi, e) != CURLM_OK)
        rc = CURLE_FAILED_INIT;
    if(rc != CURLE_OK) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, curl_easy_strerror(rc));
        finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, rc);
        return;
    }
    s->started = true;
    if(s->ws)
        c->ws_count++;
}

static bool utf8_valid(const uint8_t *p, size_t n)
{
    size_t i = 0;
    while(i < n) {
        uint8_t b = p[i];
        if(b < 0x80) {
            ++i;
            continue;
        }
        size_t extra;
        uint32_t min;
        uint32_t cp;
        if((b & 0xE0) == 0xC0) {
            extra = 1;
            min = 0x80;
            cp = b & 0x1F;
        } else if((b & 0xF0) == 0xE0) {
            extra = 2;
            min = 0x800;
            cp = b & 0x0F;
        } else if((b & 0xF8) == 0xF0) {
            extra = 3;
            min = 0x10000;
            cp = b & 0x07;
        } else {
            return false;
        }
        if(n - i <= extra)
            return false;
        for(size_t k = 1; k <= extra; ++k) {
            if((p[i + k] & 0xC0) != 0x80)
                return false;
            cp = cp << 6 | (p[i + k] & 0x3F);
        }
        if(cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
            return false;
        i += extra + 1;
    }
    return true;
}

static bool close_code_ok(uint32_t code)
{
    return (code >= 1000 && code <= 1003) || (code >= 1007 && code <= 1014) || (code >= 3000 && code <= 4999);
}

static void send_ring_read(const struct ws *w, uint32_t pos, void *out, uint32_t n)
{
    uint32_t first = w->send_cap - pos < n ? w->send_cap - pos : n;
    memcpy(out, w->send + pos, first);
    memcpy((uint8_t *)out + first, w->send, n - first);
}

/* Caller holds client->lock. A message already partly on the wire must finish: the frame cannot be cut. */
static void ws_drop_queue(struct ws *w)
{
    w->send_used = w->out_kind == OUT_MESSAGE ? (uint32_t)(8 + w->out_len) : 0;
}

static void ws_begin_close(struct client *c, struct ws *w, uint32_t state, uint32_t code, const char *reason)
{
    plat_mutex_lock(&c->lock);
    if(w->state == WS_OPEN) {
        size_t n = 0;
        if(code) {
            w->close_out[0] = (uint8_t)(code >> 8);
            w->close_out[1] = (uint8_t)code;
            n = strlen(reason);
            memcpy(w->close_out + 2, reason, n);
            n += 2;
        }
        w->close_out_len = (uint32_t)n;
        w->state = state;
        w->deadline_us = plat_now_us() + (int64_t)w->close_timeout_ms * 1000;
        ws_drop_queue(w);
    }
    plat_mutex_unlock(&c->lock);
    w->part_len = 0;
}

static void ws_protocol_close(struct client *c, struct ws *w, uint32_t code, const char *reason)
{
    w->proto_error = true;
    ws_begin_close(c, w, WS_CLOSING, code, reason);
}

static void ws_remote_close(struct client *c, struct slot *s)
{
    struct ws *w = s->ws;
    uint32_t code = 1005;
    bool bad = w->ctl_len == 1;
    if(w->ctl_len >= 2) {
        code = (uint32_t)w->ctl[0] << 8 | w->ctl[1];
        bad = !close_code_ok(code);
    }
    bool bad_text = !bad && w->ctl_len > 2 && !utf8_valid(w->ctl + 2, w->ctl_len - 2);
    w->remote_closed = true;
    w->proto_error |= bad || bad_text;
    if(!w->first_code) {
        w->first_code = code;
        w->first_detail = UGO_DETAIL_WS_CLOSED_BY_REMOTE;
        w->first_reason_len = w->ctl_len > 2 && !bad && !bad_text ? w->ctl_len - 2 : 0;
        memcpy(w->first_reason, w->ctl + 2, w->first_reason_len);
    }
    plat_mutex_lock(&c->lock);
    uint32_t state = w->state;
    if(state == WS_CLOSING)
        ws_drop_queue(w);
    plat_mutex_unlock(&c->lock);
    if(state != WS_OPEN)
        return;
    if(bad || bad_text)
        ws_begin_close(c, w, WS_CLOSING, bad ? 1002 : 1007, bad ? "invalid close frame" : "invalid UTF-8 in close reason");
    else
        ws_begin_close(c, w, WS_ECHOING, code == 1005 ? 0 : code, "");
}

/*
 * Contiguous backlog room after the partial message, or false when reading must pause. Caller holds client->lock.
 * *move: the partial moves to offset 0 first. Room 0 with true: the partial fills the whole backlog.
 */
static bool ws_room(struct ws *w, uint64_t *room, bool *move)
{
    *move = false;
    if(w->emitted - w->released >= w->count_cap)
        return false;
    uint64_t p = w->part_off, l = w->part_len, cap = w->backlog_cap;
    if(!w->unreleased_bytes) {
        *move = p != 0;
        *room = cap - l;
        return true;
    }
    uint32_t i = w->msg_head;
    while(!w->msgs[i].len)
        i = (i + 1) % w->count_cap;
    uint64_t a = w->msgs[i].off;
    if(a >= p) {
        *room = a - p - l;
        return *room > 0;
    }
    if(p + l < cap) {
        *room = cap - p - l;
        return true;
    }
    if(l < a) {
        *move = true;
        *room = a - l;
        return true;
    }
    return false;
}

static void ws_emit(struct client *c, struct slot *s, uint32_t type)
{
    struct ws *w = s->ws;
    plat_mutex_lock(&c->lock);
    struct ws_msg *m = &w->msgs[(w->msg_head + (uint32_t)(w->emitted - w->released)) % w->count_cap];
    m->off = w->part_off;
    m->len = w->part_len;
    m->time_us = plat_now_us();
    m->type = type;
    w->emitted++;
    w->unreleased_bytes += w->part_len;
    queue_event(c, s);
    plat_mutex_unlock(&c->lock);
    w->part_off += w->part_len;
    w->part_len = 0;
}

static void ws_frame(struct client *c, struct slot *s, const struct curl_ws_frame *m, const uint8_t *data, size_t n,
                     bool in_backlog)
{
    struct ws *w = s->ws;
    plat_mutex_lock(&c->lock);
    uint32_t state = w->state;
    plat_mutex_unlock(&c->lock);
    if(m->flags & (CURLWS_PING | CURLWS_PONG | CURLWS_CLOSE)) {
        if(m->offset == 0)
            w->ctl_len = 0;
        if(n > WS_CONTROL_MAX - w->ctl_len)
            n = WS_CONTROL_MAX - w->ctl_len;
        memcpy(w->ctl + w->ctl_len, data, n);
        w->ctl_len += (uint32_t)n;
        if(m->bytesleft)
            return;
        if(m->flags & CURLWS_CLOSE) {
            ws_remote_close(c, s);
        } else if((m->flags & CURLWS_PING) && state == WS_OPEN) {
            memcpy(w->pong, w->ctl, w->ctl_len);
            w->pong_len = w->ctl_len;
            w->pong_pending = true;
        }
        return;
    }
    if(state != WS_OPEN)
        return;
    if(w->part_len + n + (uint64_t)m->bytesleft > w->max_message || (n && !in_backlog)) {
        ws_protocol_close(c, w, 1009, "message too big");
        return;
    }
    w->part_len += n;
    if(m->bytesleft || (m->flags & CURLWS_CONT))
        return;
    bool binary = m->flags & CURLWS_BINARY;
    if(!binary && !utf8_valid(w->backlog + w->part_off, (size_t)w->part_len)) {
        ws_protocol_close(c, w, 1007, "invalid UTF-8 in text message");
        return;
    }
    ws_emit(c, s, binary ? UGO_WS_BINARY : UGO_WS_TEXT);
}

static CURLcode ws_receive(struct client *c, struct slot *s)
{
    struct ws *w = s->ws;
    w->rx_blocked = false;
    w->rx_more = false;
    for(int calls = 0;; ++calls) {
        if(w->remote_closed || w->proto_error)
            return CURLE_OK;
        if(calls == WS_RECV_PER_PASS) {
            w->rx_more = true;
            return CURLE_OK;
        }
        uint8_t *target = c->drop;
        size_t room = sizeof(c->drop);
        bool in_backlog = false;
        uint64_t space = 0;
        bool move = false;
        plat_mutex_lock(&c->lock);
        bool open = w->state == WS_OPEN;
        if(open && !ws_room(w, &space, &move)) {
            w->rx_paused = true;
            w->rx_blocked = true;
            plat_mutex_unlock(&c->lock);
            return CURLE_OK;
        }
        plat_mutex_unlock(&c->lock);
        if(move) {
            memmove(w->backlog, w->backlog + w->part_off, (size_t)w->part_len);
            w->part_off = 0;
        }
        if(space) {
            target = w->backlog + w->part_off + w->part_len;
            room = space > SIZE_MAX ? SIZE_MAX : (size_t)space;
            in_backlog = true;
        }
        size_t n = 0;
        const struct curl_ws_frame *meta = NULL;
        CURLcode rc = curl_ws_recv(s->easy, target, room, &n, &meta);
        if(rc == CURLE_AGAIN)
            return CURLE_OK;
        if(rc)
            return rc;
        ws_frame(c, s, meta, target, n, in_backlog);
    }
}

static CURLcode ws_send_frames(struct client *c, struct slot *s)
{
    struct ws *w = s->ws;
    w->want_write = false;
    w->tx_more = false;
    for(uint64_t spent = 0;;) {
        if(w->out_kind == OUT_NONE) {
            if(w->close_sent)
                return CURLE_OK;
            if(spent >= w->send_cap) {
                w->tx_more = true;
                return CURLE_OK;
            }
            plat_mutex_lock(&c->lock);
            uint32_t state = w->state;
            if(w->pong_pending && state == WS_OPEN) {
                memcpy(w->out_ctl, w->pong, w->pong_len);
                w->out_len = w->pong_len;
                w->out_kind = OUT_PONG;
                w->pong_pending = false;
            } else if(w->send_used && (state == WS_OPEN || state == WS_CLOSING)) {
                uint64_t header;
                send_ring_read(w, w->send_head, &header, 8);
                w->out_len = (uint32_t)header;
                w->out_type = (uint32_t)(header >> 32);
                w->out_kind = OUT_MESSAGE;
            } else if(state == WS_CLOSING || state == WS_ECHOING) {
                memcpy(w->out_ctl, w->close_out, w->close_out_len);
                w->out_len = w->close_out_len;
                w->out_kind = OUT_CLOSE;
            }
            plat_mutex_unlock(&c->lock);
            if(w->out_kind == OUT_NONE)
                return CURLE_OK;
            w->out_sent = 0;
        }
        const uint8_t *data;
        size_t length;
        curl_off_t total = 0;
        unsigned flags;
        if(w->out_kind == OUT_MESSAGE) {
            uint32_t pos = (uint32_t)((w->send_head + 8 + w->out_sent) % w->send_cap);
            data = w->send + pos;
            length = (size_t)(w->out_len - w->out_sent);
            if(length > w->send_cap - pos)
                length = w->send_cap - pos;
            total = w->out_sent ? 0 : (curl_off_t)w->out_len;
            flags = (w->out_type == UGO_WS_BINARY ? CURLWS_BINARY : CURLWS_TEXT) | CURLWS_OFFSET;
        } else {
            data = w->out_ctl + w->out_sent;
            length = (size_t)(w->out_len - w->out_sent);
            flags = w->out_kind == OUT_PONG ? CURLWS_PONG : CURLWS_CLOSE;
        }
        size_t sent = 0;
        CURLcode rc = curl_ws_send(s->easy, data, length, &sent, total, flags);
        if(rc && rc != CURLE_AGAIN)
            return rc;
        w->out_sent += sent;
        spent += sent;
        if(rc == CURLE_AGAIN || sent < length) {
#if defined(UGO_TEST_COUNTERS)
            if(w->out_sent)
                __atomic_fetch_add(&ugo_test_ws_partial_sends, 1u, __ATOMIC_RELAXED);
#endif
            w->want_write = true;
            return CURLE_OK;
        }
        if(w->out_sent < w->out_len)
            continue;
        if(w->out_kind == OUT_MESSAGE) {
            spent += 8;
            plat_mutex_lock(&c->lock);
            w->send_head = (uint32_t)((w->send_head + 8 + w->out_len) % w->send_cap);
            w->send_used -= (uint32_t)(8 + w->out_len);
            plat_mutex_unlock(&c->lock);
        } else if(w->out_kind == OUT_CLOSE) {
            w->close_sent = true;
            if(!w->first_code) {
                w->first_code = w->out_len >= 2 ? (uint32_t)w->out_ctl[0] << 8 | w->out_ctl[1] : 1005;
                w->first_detail = UGO_DETAIL_WS_CLOSED_BY_LOCAL;
                w->first_reason_len = w->out_len > 2 ? (uint32_t)w->out_len - 2 : 0;
                memcpy(w->first_reason, w->out_ctl + 2, w->first_reason_len);
            }
        }
        w->out_kind = OUT_NONE;
    }
}

static void ws_service(struct client *c, struct slot *s)
{
    struct ws *w = s->ws;
    CURLcode rc = ws_receive(c, s);
    if(rc) {
        set_error(s, UGO_CLASS_TRANSFER, UGO_DETAIL_NONE,
                  rc == CURLE_GOT_NOTHING ? "connection closed without a close handshake" : curl_easy_strerror(rc));
        finish(c, s, UGO_CLASS_TRANSFER, UGO_DETAIL_NONE, rc);
        return;
    }
    rc = ws_send_frames(c, s);
    if(rc) {
        /* The server closed first and may drop the connection before our echo lands. */
        if(w->remote_closed && !w->proto_error) {
            finish(c, s, UGO_CLASS_NONE, UGO_DETAIL_NONE, CURLE_OK);
        } else {
            set_error(s, UGO_CLASS_TRANSFER, UGO_DETAIL_NONE, curl_easy_strerror(rc));
            finish(c, s, UGO_CLASS_TRANSFER, UGO_DETAIL_NONE, rc);
        }
        return;
    }
    plat_mutex_lock(&c->lock);
    int64_t deadline = w->deadline_us;
    plat_mutex_unlock(&c->lock);
    if(w->close_sent && (w->remote_closed || w->proto_error)) {
        finish(c, s, w->proto_error ? UGO_CLASS_TRANSFER : UGO_CLASS_NONE, UGO_DETAIL_NONE, CURLE_OK);
    } else if(deadline && plat_now_us() >= deadline) {
        set_error(s, UGO_CLASS_TIMEOUT, UGO_DETAIL_NONE, "close handshake timed out");
        finish(c, s, UGO_CLASS_TIMEOUT, UGO_DETAIL_NONE, CURLE_OK);
    }
}

static void ws_opened(struct client *c, struct slot *s)
{
    struct ws *w = s->ws;
    s->final_seen = true;
    s->final_len = s->hdr_len;
    bool protocol_ok = !w->hs_protocols || w->hs_protocol;
    if(!w->hs_upgrade || !w->hs_connection || !w->hs_accept || w->hs_extensions || !protocol_ok) {
        set_error(s, UGO_CLASS_TRANSFER, UGO_DETAIL_WS_HANDSHAKE,
                  w->hs_accepts > 1 ? "more than one Sec-WebSocket-Accept in the 101 response" :
                  !w->hs_accept ? "Sec-WebSocket-Accept does not match the key" :
                  w->hs_extensions ? "server selected an extension that was not offered" :
                  !protocol_ok ? "server selected a subprotocol that was not offered" :
                  "Upgrade or Connection header missing in the 101 response");
        finish(c, s, UGO_CLASS_TRANSFER, UGO_DETAIL_WS_HANDSHAKE, CURLE_OK);
        return;
    }
    curl_socket_t fd = CURL_SOCKET_BAD;
    if(curl_easy_getinfo(s->easy, CURLINFO_ACTIVESOCKET, &fd) != CURLE_OK || fd == CURL_SOCKET_BAD) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, "no socket after the WebSocket handshake");
        finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, CURLE_OK);
        return;
    }
    w->fd = fd;
    plat_mutex_lock(&c->lock);
    w->state = WS_OPEN;
    w->deadline_us = 0;
    plat_mutex_unlock(&c->lock);
    emit_headers(c, s);
}

static bool ws_redirect(struct client *c, struct slot *s);
static void http_see_other(struct client *c, struct slot *s);

static void ws_service_all(struct client *c)
{
    for(uint32_t i = 0; c->ws_count && i < c->max; ++i) {
        struct slot *s = &c->slots[i];
        if(!s->started || !s->ws)
            continue;
        plat_mutex_lock(&c->lock);
        bool live = s->state == SLOT_ACTIVE && !s->abort;
        bool connecting = s->ws->state == WS_CONNECTING;
        int64_t deadline = s->ws->deadline_us;
        plat_mutex_unlock(&c->lock);
        if(!live)
            continue;
        if(!connecting) {
            ws_service(c, s);
        } else if(deadline && plat_now_us() >= deadline) {
            set_error(s, UGO_CLASS_TIMEOUT, UGO_DETAIL_NONE, "WebSocket open timed out");
            finish(c, s, UGO_CLASS_TIMEOUT, UGO_DETAIL_NONE, CURLE_OPERATION_TIMEDOUT);
        }
    }
}

/* After a budget yield, data may sit in curl's buffers with nothing left on the socket to wake the poll. */
static unsigned ws_poll_set(struct client *c, int *timeout_ms)
{
    unsigned n = 0;
    int64_t now = plat_now_us();
    for(uint32_t i = 0; c->ws_count && i < c->max; ++i) {
        struct slot *s = &c->slots[i];
        if(!s->started || !s->ws)
            continue;
        struct ws *w = s->ws;
        plat_mutex_lock(&c->lock);
        bool live = s->state == SLOT_ACTIVE;
        bool connecting = w->state == WS_CONNECTING;
        int64_t deadline = w->deadline_us;
        plat_mutex_unlock(&c->lock);
        if(!live)
            continue;
        if(deadline) {
            int64_t left = (deadline - now + 999) / 1000;
            if(left < *timeout_ms)
                *timeout_ms = left < 0 ? 0 : (int)left;
        }
        if(connecting)
            continue;
        if(w->rx_more || w->tx_more)
            *timeout_ms = 0;
        short events = (short)((!w->rx_blocked && !w->remote_closed && !w->proto_error ? CURL_WAIT_POLLIN : 0) |
                               (w->want_write ? CURL_WAIT_POLLOUT : 0));
        if(!events)
            continue;
        c->waitfds[n].fd = w->fd;
        c->waitfds[n].events = events;
        c->waitfds[n].revents = 0;
        ++n;
    }
    return n;
}

static void finish_all(struct client *c, uint32_t cls, uint32_t detail)
{
    for(uint32_t i = 0; i < c->max; ++i) {
        struct slot *s = &c->slots[i];
        plat_mutex_lock(&c->lock);
        bool active = s->state == SLOT_ACTIVE;
        bool aborted = s->abort;
        plat_mutex_unlock(&c->lock);
        if(!active)
            continue;
        if(aborted)
            finish(c, s, UGO_CLASS_ABORTED, UGO_DETAIL_CALLER, CURLE_OK);
        else
            finish(c, s, cls, detail, CURLE_OK);
    }
}

void owner_main(void *arg)
{
    struct client *c = arg;
    for(;;) {
        plat_mutex_lock(&c->lock);
        while(c->broken && !c->shutting_down && !c->owner_count)
            plat_cond_wait(&c->owner_cond, &c->lock);
        bool stop = c->shutting_down;
        uint32_t n = 0;
        while(c->owner_count) {
            uint32_t index = c->owner_ring[c->owner_head];
            c->owner_head = (c->owner_head + 1) % c->max;
            c->owner_count--;
            c->slots[index].owner_queued = false;
            c->scratch[n++] = index;
        }
        plat_mutex_unlock(&c->lock);
        if(stop)
            break;
        for(uint32_t i = 0; i < n; ++i) {
            struct slot *s = &c->slots[c->scratch[i]];
            plat_mutex_lock(&c->lock);
            bool active = s->state == SLOT_ACTIVE;
            bool aborted = s->abort;
            bool expired = s->bg_expired;
            plat_mutex_unlock(&c->lock);
            if(!active)
                continue;
            if(aborted)
                finish(c, s, UGO_CLASS_ABORTED, UGO_DETAIL_CALLER, CURLE_OK);
            else if(expired)
                finish(c, s, UGO_CLASS_ABORTED, UGO_DETAIL_BACKGROUND_EXPIRED, CURLE_OK);
            else if(c->broken)
                finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, CURLE_OK);
            else if(!s->started)
                start(c, s);
        }
        if(c->broken)
            continue;
        int running = 0;
        CURLMcode mc = curl_multi_perform(c->multi, &running);
        c->performs++;
        CURLMsg *msg;
        int left;
        while(mc == CURLM_OK && (msg = curl_multi_info_read(c->multi, &left))) {
            if(msg->msg != CURLMSG_DONE)
                continue;
            char *slot = NULL;
            curl_easy_getinfo(msg->easy_handle, CURLINFO_PRIVATE, &slot);
            struct slot *done = (struct slot *)slot;
            if(done->ws && msg->data.result == CURLE_OK && !done->fail_class)
                ws_opened(c, done);
            else if(!done->ws && msg->data.result == CURLE_ABORTED_BY_CALLBACK && done->see_other && !done->fail_class)
                http_see_other(c, done);
            else if(!done->ws || msg->data.result != CURLE_HTTP_RETURNED_ERROR || done->fail_class ||
                    !ws_redirect(c, done))
                finish_classified(c, done, msg->data.result);
        }
        if(mc == CURLM_OK) {
            ws_service_all(c);
            int timeout_ms = POLL_MS;
            unsigned fds = ws_poll_set(c, &timeout_ms);
            mc = curl_multi_poll(c->multi, c->waitfds, fds, timeout_ms, NULL);
        }
        if(mc != CURLM_OK) {
            c->broken = true;
            finish_all(c, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE);
        }
    }
    finish_all(c, UGO_CLASS_ABORTED, UGO_DETAIL_SHUTDOWN);
    plat_mutex_lock(&c->lock);
    c->multi_alive = false;
    plat_mutex_unlock(&c->lock);
    curl_multi_cleanup(c->multi);
    c->multi = NULL;
}

static void client_free(struct client *c)
{
    if(c->slots) {
        for(uint32_t i = 0; i < c->max; ++i) {
            struct slot *s = &c->slots[i];
            if(s->easy)
                curl_easy_cleanup(s->easy);
            free_req(s->req);
            free(s->body);
            free(s->hdr);
            free(s->ws);
        }
    }
    if(c->multi)
        curl_multi_cleanup(c->multi);
    free(c->slots);
    free(c->free_stack);
    free(c->owner_ring);
    free(c->event_ring);
    free(c->scratch);
    free(c->waitfds);
    mbedtls_x509_crt_free(&c->ca);
    free(c->ca_copy);
    free(c);
}

int32_t ugo_client_create(const ugo_client_config *config, ugo_client **out_client)
{
    if(!config || !out_client)
        return UGO_E_INVALID_ARG;
    if(config->abi_version != UGO_ABI_VERSION)
        return UGO_E_ABI;
    if(!config->max_transfers || config->reserved || (config->flags & ~CLIENT_FLAGS) ||
       !bytes_ok(config->ca_bundle))
        return UGO_E_INVALID_ARG;
    if(plat_once(process_init) || !process_ready)
        return UGO_E_NOMEM;

    struct client *c = calloc(1, sizeof(*c));
    if(!c)
        return UGO_E_NOMEM;
    mbedtls_x509_crt_init(&c->ca);
    uint32_t max = config->max_transfers;
    c->max = max;
    c->dns_ttl_s = config->dns_cache_ttl_s;
    c->slots = calloc(max, sizeof(*c->slots));
    c->free_stack = calloc(max, sizeof(uint32_t));
    c->owner_ring = calloc(max, sizeof(uint32_t));
    c->event_ring = calloc(max, sizeof(uint32_t));
    c->scratch = calloc(max, sizeof(uint32_t));
    c->waitfds = calloc(max, sizeof(struct curl_waitfd));
    if(!c->slots || !c->free_stack || !c->owner_ring || !c->event_ring || !c->scratch || !c->waitfds) {
        client_free(c);
        return UGO_E_NOMEM;
    }
    for(uint32_t i = 0; i < max; ++i) {
        struct slot *s = &c->slots[i];
        s->generation = 1;
        s->index = i;
        s->client = c;
        s->sink = PLAT_NO_FILE;
        c->free_stack[i] = max - 1 - i;
    }
    c->free_count = max;
    /* mbedtls_x509_crt_parse takes a buffer as PEM only when its last byte is '\0' */
    if(config->ca_bundle.len) {
        size_t len = (size_t)config->ca_bundle.len;
        const char *ca = bytes_ptr(config->ca_bundle);
        bool terminated = ca[len - 1] == '\0';
        c->ca_copy = malloc(len + !terminated);
        if(!c->ca_copy) {
            client_free(c);
            return UGO_E_NOMEM;
        }
        memcpy(c->ca_copy, ca, len);
        if(!terminated)
            c->ca_copy[len++] = '\0';
        c->ca_pem = c->ca_copy;
        c->ca_len = len;
    } else {
        c->ca_pem = ugo_ca_bundle;
        c->ca_len = sizeof(ugo_ca_bundle);
    }
    c->insecure = config->flags & UGO_CLIENT_TLS_INSECURE_DEV;
    c->multi = curl_multi_init();
    if(!c->multi ||
       curl_multi_setopt(c->multi, CURLMOPT_MAX_TOTAL_CONNECTIONS, to_long(config->max_total_connections)) != CURLM_OK) {
        client_free(c);
        return UGO_E_NOMEM;
    }
    c->multi_alive = true;
    if(plat_mutex_init(&c->lock)) {
        client_free(c);
        return UGO_E_NOMEM;
    }
    if(plat_cond_init(&c->events_cond) || plat_cond_init(&c->joined_cond) || plat_cond_init(&c->owner_cond) ||
       plat_thread_start(&c->thread, c)) {
        plat_mutex_destroy(&c->lock);
        client_free(c);
        return UGO_E_NOMEM;
    }
    plat_mutex_lock(&live_lock);
    c->next = live;
    live = c;
    plat_mutex_unlock(&live_lock);
    *out_client = (ugo_client *)c;
    return UGO_OK;
}

int32_t ugo_client_shutdown(ugo_client *client)
{
    struct client *c = (struct client *)client;
    if(!c)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    if(!c->shutting_down) {
        c->shutting_down = true;
        wake_owner(c);
        plat_cond_broadcast(&c->events_cond);
        plat_mutex_unlock(&c->lock);
        plat_thread_join(c->thread);
        plat_mutex_lock(&c->lock);
        c->joined = true;
        plat_cond_broadcast(&c->events_cond);
        plat_cond_broadcast(&c->joined_cond);
    }
    while(!c->joined)
        plat_cond_wait(&c->joined_cond, &c->lock);
    plat_mutex_unlock(&c->lock);
    return UGO_OK;
}

static void destroy_unlisted(struct client *c)
{
    ugo_client_shutdown((ugo_client *)c);
    plat_cond_destroy(&c->events_cond);
    plat_cond_destroy(&c->joined_cond);
    plat_cond_destroy(&c->owner_cond);
    plat_mutex_destroy(&c->lock);
    client_free(c);
}

void ugo_client_destroy(ugo_client *client)
{
    struct client *c = (struct client *)client;
    if(!c)
        return;
    plat_mutex_lock(&live_lock);
    struct client **p = &live;
    while(*p && *p != c)
        p = &(*p)->next;
    bool found = *p != NULL;
    if(found)
        *p = c->next;
    plat_mutex_unlock(&live_lock);
    if(found)
        destroy_unlisted(c);
}

int32_t ugo_shutdown_all(void)
{
    plat_mutex_lock(&live_lock);
    struct client *list = live;
    live = NULL;
    plat_mutex_unlock(&live_lock);
    int32_t n = 0;
    while(list) {
        struct client *c = list;
        list = c->next;
        destroy_unlisted(c);
        ++n;
    }
    return n;
}

static char *put_text(char **cursor, ugo_bytes b)
{
    char *p = *cursor;
    if(b.len)
        memcpy(p, bytes_ptr(b), (size_t)b.len);
    p[b.len] = 0;
    *cursor += b.len + 1;
    return p;
}

static bool method_is(ugo_bytes method, const char *name)
{
    return method.len == strlen(name) && !memcmp(bytes_ptr(method), name, (size_t)method.len);
}

static bool valid_method(ugo_bytes method)
{
    if(!bytes_ok(method) || !method.len || method.len > UGO_METHOD_MAX)
        return false;
    const char *p = bytes_ptr(method);
    for(size_t i = 0; i < method.len; ++i) {
        char ch = p[i];
        bool tchar = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') ||
                     (ch && strchr("!#$%&'*+-.^_`|~", ch));
        if(!tchar)
            return false;
    }
    return !method_is(method, "CONNECT");
}

static bool valid_request(const ugo_request *r)
{
    if(!valid_method(r->method) || (method_is(r->method, "HEAD") && r->body_kind != UGO_BODY_NONE))
        return false;
    if((r->flags & ~REQUEST_FLAGS) || !r->connect_timeout_ms || r->low_speed_time_s > 65535 ||
       !r->low_speed_bytes_per_s != !r->low_speed_time_s)
        return false;
    if(!bytes_ok(r->url) || !r->url.len || has_nul(r->url) || !bytes_ok(r->headers) || !bytes_ok(r->body) || !bytes_ok(r->sink_path) || has_nul(r->sink_path))
        return false;
    bool detached = r->flags & UGO_REQ_DETACHED;
    if(detached && r->sink_path.len)
        return false;
    if((r->flags & UGO_REQ_RESUME) && (!r->sink_path.len || method_is(r->method, "HEAD")))
        return false;
    if(!detached && !r->sink_path.len && !r->memory_limit)
        return false;
    switch(r->body_kind) {
    case UGO_BODY_NONE:
        if(r->body.len)
            return false;
        break;
    case UGO_BODY_MEMORY:
        break;
    case UGO_BODY_FILE:
        if(!r->body.len || has_nul(r->body))
            return false;
        break;
    case UGO_BODY_MULTIPART:
        if(r->body.len || !r->part_count || !r->parts)
            return false;
        break;
    default:
        return false;
    }
    if(r->body_kind != UGO_BODY_MULTIPART && (r->parts || r->part_count))
        return false;
    const ugo_mime_part *parts = (const ugo_mime_part *)(uintptr_t)r->parts;
    for(uint32_t i = 0; i < r->part_count; ++i) {
        const ugo_mime_part *p = &parts[i];
        if(p->reserved || (p->source != UGO_PART_MEMORY && p->source != UGO_PART_FILE))
            return false;
        if(!bytes_ok(p->name) || !p->name.len || has_nul(p->name) || !bytes_ok(p->filename) ||
           has_nul(p->filename) || !bytes_ok(p->content_type) || has_nul(p->content_type) ||
           !bytes_ok(p->data))
            return false;
        if(p->source == UGO_PART_FILE && (!p->data.len || has_nul(p->data)))
            return false;
    }
    return true;
}

static int32_t parse_headers(ugo_bytes headers, char *scratch, struct curl_slist **list)
{
    if(!headers.len)
        return UGO_OK;
    const char *p = bytes_ptr(headers);
    const char *end = p + headers.len;
    while(p < end) {
        if(*p == ' ' || *p == '\t')
            return UGO_E_INVALID_ARG;
        const char *eol = memchr(p, '\n', (size_t)(end - p));
        const char *next = eol ? eol + 1 : end;
        if(!eol)
            eol = end;
        else if(eol > p && eol[-1] == '\r')
            --eol;
        if(memchr(p, '\r', (size_t)(eol - p)) || memchr(p, 0, (size_t)(eol - p)))
            return UGO_E_INVALID_ARG;
        if(eol > p) {
            memcpy(scratch, p, (size_t)(eol - p));
            scratch[eol - p] = 0;
            struct curl_slist *grown = curl_slist_append(*list, scratch);
            if(!grown)
                return UGO_E_NOMEM;
            *list = grown;
        }
        p = next;
    }
    return UGO_OK;
}

/* curl trims blanks between the name and the colon, so "Authorization :" is that header too. */
static bool header_named(const char *line, const char *name)
{
    size_t n = strlen(name);
    if(!curl_strnequal(line, name, n))
        return false;
    while(line[n] == ' ' || line[n] == '\t')
        ++n;
    return line[n] == ':' || line[n] == ';';
}

static bool has_header(const struct curl_slist *list, const char *name)
{
    for(; list; list = list->next)
        if(header_named(list->data, name))
            return true;
    return false;
}

/* Appends `line` unless the caller sent a header `name`; "Name:" makes curl drop its own header of that name. */
static struct curl_slist *add_unless(struct curl_slist *list, const char *name, const char *line, bool *failed)
{
    if(has_header(list, name))
        return list;
    struct curl_slist *grown = curl_slist_append(list, line);
    if(!grown)
        *failed = true;
    return grown ? grown : list;
}

static bool url_ok(CURLU *url, const char *text, bool ws)
{
    if(curl_url_set(url, CURLUPART_URL, text, CURLU_NON_SUPPORT_SCHEME) != CURLUE_OK)
        return false;
    char *scheme = NULL;
    if(curl_url_get(url, CURLUPART_SCHEME, &scheme, 0) != CURLUE_OK)
        return false;
    bool ok = ws ? !strcmp(scheme, "ws") || !strcmp(scheme, "wss") : !strcmp(scheme, "http") || !strcmp(scheme, "https");
    curl_free(scheme);
    char *host = NULL;
    if(ok && curl_url_get(url, CURLUPART_HOST, &host, 0) != CURLUE_OK)
        ok = false;
    curl_free(host);
    return ok;
}

static bool add_text(size_t *size, ugo_bytes b)
{
    if(b.len >= SIZE_MAX - *size)
        return false;
    *size += (size_t)b.len + 1;
    return true;
}

/* Main thread, from the iOS expiration handler. The client may be in shutdown or already destroyed. */
static void on_bg_expired(void *ctx, void *bg)
{
    plat_mutex_lock(&live_lock);
    struct client *c = live;
    while(c && c != ctx)
        c = c->next;
    if(c) {
        plat_mutex_lock(&c->lock);
        for(uint32_t i = 0; i < c->max; ++i) {
            struct slot *s = &c->slots[i];
            if(s->bg == bg && s->state == SLOT_ACTIVE) {
                s->bg_expired = true;
                queue_owner(c, s);
                break;
            }
        }
        plat_mutex_unlock(&c->lock);
    }
    plat_mutex_unlock(&live_lock);
}

/* Takes a free slot for r (and w) or frees them; both are owned by the slot from then on. */
static int32_t claim_slot(struct client *c, struct req *r, struct ws *w, void *bg, bool detached, uint64_t *out_id)
{
    plat_mutex_lock(&c->lock);
    if(c->shutting_down || !c->free_count) {
        int32_t result = c->shutting_down ? UGO_E_SHUTDOWN : UGO_E_CAPACITY;
        plat_mutex_unlock(&c->lock);
        plat_bg_end(bg);
        free_req(r);
        free(w);
        return result;
    }
    struct slot *s = &c->slots[c->free_stack[--c->free_count]];
    s->state = SLOT_ACTIVE;
    s->detached = detached;
    s->abort = false;
    s->bg = bg;
    s->bg_expired = plat_bg_has_expired(bg);
    s->req = r;
    s->ws = w;
    s->body = NULL;
    s->body_len = 0;
    s->body_cap = 0;
    s->hdr_len = 0;
    s->final_len = 0;
    s->final_seen = false;
    s->error[0] = 0;
    s->has_location = false;
    s->see_other = false;
    s->prior_redirects = 0;
    s->prior_us = 0;
    s->prior_connected = false;
    s->sink_opened = false;
    s->status = 0;
    s->version = UGO_HTTP_NONE;
    s->content_length = -1;
    s->fail_class = UGO_CLASS_NONE;
    s->fail_detail = UGO_DETAIL_NONE;
    s->last_rx = 0;
    s->last_tx = 0;
    s->progress_us = 0;
    s->progress_held = false;
    s->resume_from = 0;
    s->resume_base = 0;
    s->range_left = 0;
    s->resume_bad = false;
    s->range_count = 0;
    s->range_multipart = false;
    *out_id = (uint64_t)s->generation << 32 | s->index;
    queue_owner(c, s);
    plat_mutex_unlock(&c->lock);
    return UGO_OK;
}

int32_t ugo_submit(ugo_client *client, const ugo_request *request, uint64_t *out_id)
{
    struct client *c = (struct client *)client;
    if(!c || !request || !out_id || !valid_request(request))
        return UGO_E_INVALID_ARG;
    const ugo_request *q = request;
    const ugo_mime_part *parts = (const ugo_mime_part *)(uintptr_t)q->parts;
    bool detached = q->flags & UGO_REQ_DETACHED;

    size_t size = sizeof(struct req);
    bool fits = q->part_count <= (SIZE_MAX - size) / sizeof(struct part);
    if(fits)
        size += (size_t)q->part_count * sizeof(struct part);
    fits = fits && add_text(&size, q->method) && add_text(&size, q->url) && add_text(&size, q->headers) &&
           add_text(&size, q->sink_path);
    if(detached || q->body_kind == UGO_BODY_FILE)
        fits = fits && add_text(&size, q->body);
    for(uint32_t i = 0; fits && i < q->part_count; ++i) {
        fits = add_text(&size, parts[i].name) && add_text(&size, parts[i].filename) &&
               add_text(&size, parts[i].content_type);
        if(detached || parts[i].source == UGO_PART_FILE)
            fits = fits && add_text(&size, parts[i].data);
    }
    if(!fits)
        return UGO_E_INVALID_ARG;
    struct req *r = calloc(1, size);
    if(!r)
        return UGO_E_NOMEM;
    r->parts = (struct part *)(r + 1);
    char *cursor = (char *)(r->parts + q->part_count);
    r->body_kind = q->body_kind;
    r->flags = q->flags;
    r->part_count = q->part_count;
    r->memory_limit = q->memory_limit;
    r->max_recv = q->max_recv_bytes_per_s;
    r->max_send = q->max_send_bytes_per_s;
    r->low_speed_bytes = q->low_speed_bytes_per_s;
    r->low_speed_time_s = q->low_speed_time_s;
    r->connect_timeout_ms = q->connect_timeout_ms;
    r->total_timeout_ms = q->total_timeout_ms;
    r->max_redirects = q->max_redirects;
    r->body.file = PLAT_NO_FILE;
    r->head = method_is(q->method, "HEAD");
    const char *plain = q->body_kind == UGO_BODY_NONE ? "GET" : "POST";
    if(!r->head && !method_is(q->method, plain))
        r->custom_method = put_text(&cursor, q->method);
    if(q->sink_path.len)
        r->sink_path = put_text(&cursor, q->sink_path);
    if(q->body_kind == UGO_BODY_FILE) {
        r->body.path = put_text(&cursor, q->body);
    } else if(q->body_kind == UGO_BODY_MEMORY) {
        r->body.len = q->body.len;
        r->body.data = detached ? (const uint8_t *)put_text(&cursor, q->body) : bytes_ptr(q->body);
    }
    for(uint32_t i = 0; i < q->part_count; ++i) {
        const ugo_mime_part *in = &parts[i];
        struct part *p = &r->parts[i];
        p->name = put_text(&cursor, in->name);
        p->filename = in->filename.len ? put_text(&cursor, in->filename) : NULL;
        p->type = in->content_type.len ? put_text(&cursor, in->content_type) : NULL;
        p->up.file = PLAT_NO_FILE;
        if(in->source == UGO_PART_FILE) {
            p->up.path = put_text(&cursor, in->data);
        } else {
            p->up.len = in->data.len;
            p->up.data = detached ? (const uint8_t *)put_text(&cursor, in->data) : bytes_ptr(in->data);
        }
    }
    char *url_text = put_text(&cursor, q->url);
    r->url = curl_url();
    if(!r->url) {
        free_req(r);
        return UGO_E_NOMEM;
    }
    if(!url_ok(r->url, url_text, false)) {
        free_req(r);
        return UGO_E_INVALID_ARG;
    }
    int32_t error = parse_headers(q->headers, cursor, &r->headers);
    if(error) {
        free_req(r);
        return error;
    }
    if((q->flags & UGO_REQ_RESUME) && has_header(r->headers, "Range")) {
        free_req(r);
        return UGO_E_INVALID_ARG;
    }
    bool failed = false;
    r->headers = add_unless(r->headers, "Accept", "Accept:", &failed);
    if(!failed && q->body_kind != UGO_BODY_NONE)
        r->headers = add_unless(r->headers, "Expect", "Expect:", &failed);
    if(!failed && q->body_kind != UGO_BODY_NONE && q->body_kind != UGO_BODY_MULTIPART)
        r->headers = add_unless(r->headers, "Content-Type", "Content-Type:", &failed);
    /* Ranges count bytes of the encoded representation: every attempt must get the same one, stored undecoded. */
    if(!failed && (q->flags & UGO_REQ_RESUME))
        r->headers = add_unless(r->headers, "Accept-Encoding", "Accept-Encoding: identity", &failed);
    r->decode = !has_header(r->headers, "Accept-Encoding");
    if(failed) {
        free_req(r);
        return UGO_E_NOMEM;
    }

    /* Begun outside the client lock: the expiration handler takes that lock on the main thread. */
    void *bg = q->flags & UGO_REQ_FINISH_IN_BACKGROUND ? plat_bg_begin(on_bg_expired, c) : NULL;
    return claim_slot(c, r, NULL, bg, detached, out_id);
}

int32_t ugo_abort(ugo_client *client, uint64_t id)
{
    struct client *c = (struct client *)client;
    if(!c)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = lookup(c, id);
    int32_t result = s ? UGO_OK : UGO_E_STALE_ID;
    if(s && s->state == SLOT_ACTIVE && !s->abort) {
        s->abort = true;
        queue_owner(c, s);
    }
    plat_mutex_unlock(&c->lock);
    return result;
}

int32_t ugo_get_result(ugo_client *client, uint64_t id, ugo_result *out_result)
{
    struct client *c = (struct client *)client;
    if(!c || !out_result)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = lookup(c, id);
    int32_t result = !s ? UGO_E_STALE_ID : s->state != SLOT_FINISHED ? UGO_E_STATE : UGO_OK;
    if(result == UGO_OK) {
        out_result->body.ptr = s->body_len && s->body ? (uint64_t)(uintptr_t)s->body : 0;
        out_result->body.len = s->body ? s->body_len : 0;
        out_result->headers.ptr = s->final_seen ? (uint64_t)(uintptr_t)s->hdr : 0;
        out_result->headers.len = s->final_seen ? s->final_len : 0;
        size_t n = s->error_len;
        out_result->error_message.ptr = n ? (uint64_t)(uintptr_t)s->error : 0;
        out_result->error_message.len = n;
    }
    plat_mutex_unlock(&c->lock);
    return result;
}

int32_t ugo_release(ugo_client *client, uint64_t id)
{
    struct client *c = (struct client *)client;
    if(!c)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = lookup(c, id);
    if(!s || s->state != SLOT_FINISHED) {
        plat_mutex_unlock(&c->lock);
        return s ? UGO_E_STATE : UGO_E_STALE_ID;
    }
    uint8_t *body = s->body;
    struct ws *w = s->ws;
    s->body = NULL;
    s->ws = NULL;
    s->body_len = 0;
    s->body_cap = 0;
    s->final_seen = false;
    s->final_len = 0;
    free_slot(c, s);
    plat_mutex_unlock(&c->lock);
    free(body);
    free(w);
    return UGO_OK;
}

int32_t ugo_wait_events(ugo_client *client, ugo_event *events, uint32_t capacity, uint32_t timeout_ms)
{
    struct client *c = (struct client *)client;
    if(!c || !events || !capacity)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    if(c->draining) {
        plat_mutex_unlock(&c->lock);
        return UGO_E_BUSY;
    }
    c->draining = true;
    int64_t deadline = plat_now_us() + (int64_t)timeout_ms * 1000;
    while(!c->event_count && !c->joined) {
        int64_t left = deadline - plat_now_us();
        if(left <= 0)
            break;
        /* The condvar clock stops during system sleep; the deadline clock does not. */
        plat_cond_wait_ms(&c->events_cond, &c->lock, left > 1000000 ? 1000u : (uint32_t)((left + 999) / 1000));
    }
    uint32_t n = 0;
    while(n < capacity && c->event_count) {
        struct slot *s = &c->slots[c->event_ring[c->event_head]];
        if(s->ev_headers && n < capacity) {
            events[n++] = s->headers_event;
            s->ev_headers = false;
        }
        if(s->ev_progress && n < capacity) {
            events[n++] = s->progress_event;
            s->ev_progress = false;
        }
        struct ws *w = s->ws;
        while(w && w->drained < w->emitted && n < capacity) {
            const struct ws_msg *m = &w->msgs[(w->msg_head + (uint32_t)(w->drained - w->released)) % w->count_cap];
            ugo_event *e = &events[n++];
            memset(e, 0, sizeof(*e));
            e->kind = UGO_EV_WS_MESSAGE;
            e->id = (uint64_t)s->generation << 32 | s->index;
            e->time_us = m->time_us;
            e->u.ws_message.seq = ++w->drained;
            e->u.ws_message.data.ptr = m->len ? (uint64_t)(uintptr_t)(w->backlog + m->off) : 0;
            e->u.ws_message.data.len = m->len;
            e->u.ws_message.type = m->type;
        }
        bool messages = w && w->drained < w->emitted;
        if(s->ev_completed && !messages && n < capacity) {
            events[n++] = s->completed_event;
            s->ev_completed = false;
            s->state = SLOT_FINISHED;
        }
        if(s->ev_headers || s->ev_progress || s->ev_completed || messages)
            break;
        s->event_queued = false;
        c->event_head = (c->event_head + 1) % c->max;
        c->event_count--;
    }
    int32_t result = n ? (int32_t)n : c->joined ? UGO_E_SHUTDOWN : 0;
    c->draining = false;
    plat_mutex_unlock(&c->lock);
    return result;
}

static void base64(const uint8_t *in, size_t n, char *out)
{
    static const char table[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    for(size_t i = 0; i < n; i += 3) {
        uint32_t v = (uint32_t)in[i] << 16 | (i + 1 < n ? (uint32_t)in[i + 1] << 8 : 0) | (i + 2 < n ? in[i + 2] : 0);
        *out++ = table[v >> 18 & 63];
        *out++ = table[v >> 12 & 63];
        *out++ = i + 1 < n ? table[v >> 6 & 63] : '=';
        *out++ = i + 2 < n ? table[v & 63] : '=';
    }
    *out = 0;
}

/* curl 8.23 sends a caller-set Sec-WebSocket-Key as is and never checks the Accept, so the shim owns both. */
static bool ws_key(char key[25], char accept[29])
{
    uint8_t raw[16];
    if(psa_generate_random(raw, sizeof(raw)) != PSA_SUCCESS)
        return false;
    base64(raw, sizeof(raw), key);
    uint8_t text[24 + 36];
    memcpy(text, key, 24);
    memcpy(text + 24, "258EAFA5-E914-47DA-95CA-C5AB0DC85B11", 36);
    uint8_t digest[20];
    size_t len = 0;
    if(psa_hash_compute(PSA_ALG_SHA_1, text, sizeof(text), digest, sizeof(digest), &len) != PSA_SUCCESS || len != 20)
        return false;
    base64(digest, sizeof(digest), accept);
    return true;
}

static bool same_origin(CURLU *a, CURLU *b)
{
    static const CURLUPart parts[] = {CURLUPART_SCHEME, CURLUPART_HOST, CURLUPART_PORT, CURLUPART_ZONEID};
    bool same = true;
    for(size_t i = 0; same && i < sizeof(parts) / sizeof(parts[0]); ++i) {
        char *x = NULL, *y = NULL;
        CURLUcode ac = curl_url_get(a, parts[i], &x, CURLU_DEFAULT_PORT);
        CURLUcode bc = curl_url_get(b, parts[i], &y, CURLU_DEFAULT_PORT);
        if(ac == CURLUE_OK && bc == CURLUE_OK)
            same = parts[i] == CURLUPART_ZONEID ? !strcmp(x, y) : curl_strequal(x, y);
        else
            same = parts[i] == CURLUPART_ZONEID && ac == CURLUE_NO_ZONEID && bc == CURLUE_NO_ZONEID;
        curl_free(x);
        curl_free(y);
    }
    return same;
}

#if defined(UGO_TEST_COUNTERS)
bool ugo_test_same_origin(const char *a, const char *b)
{
    CURLU *x = curl_url(), *y = curl_url();
    bool same = x && y && url_ok(x, a, true) && url_ok(y, b, true) && same_origin(x, y);
    curl_url_cleanup(x);
    curl_url_cleanup(y);
    return same;
}
#endif

static CURLcode ws_next_url(CURLU *next, const char *location)
{
    CURLUcode uc = curl_url_set(next, CURLUPART_URL, location, CURLU_URLENCODE | CURLU_ALLOW_SPACE | CURLU_NON_SUPPORT_SCHEME);
    if(uc)
        return uc == CURLUE_OUT_OF_MEMORY ? CURLE_OUT_OF_MEMORY : CURLE_URL_MALFORMAT;
    char *scheme = NULL;
    if(curl_url_get(next, CURLUPART_SCHEME, &scheme, 0) != CURLUE_OK)
        return CURLE_URL_MALFORMAT;
    const char *ws = !strcmp(scheme, "http") || !strcmp(scheme, "ws") ? "ws" :
                     !strcmp(scheme, "https") || !strcmp(scheme, "wss") ? "wss" : NULL;
    curl_free(scheme);
    if(!ws)
        return CURLE_UNSUPPORTED_PROTOCOL;
    uc = curl_url_set(next, CURLUPART_SCHEME, ws, 0);
    return uc == CURLUE_OK ? CURLE_OK : uc == CURLUE_OUT_OF_MEMORY ? CURLE_OUT_OF_MEMORY : CURLE_URL_MALFORMAT;
}

static bool origin_header(const char *line)
{
    return header_named(line, "Authorization") || header_named(line, "Cookie") || header_named(line, "Host");
}

/* curl fails every non-101 answer to an upgrade before its own redirect logic runs (http.c http_on_response),
   so the shim follows: a new connection, key and handshake per hop. Owner thread. */
static bool ws_redirect(struct client *c, struct slot *s)
{
    struct req *r = s->req;
    struct ws *w = s->ws;
    if(s->status < 300 || s->status > 399 || !s->has_location)
        return false;
    char *location = NULL;
    for(size_t at = 0; at < s->hdr_len && !location;) {
        const char *line = s->hdr + at;
        const char *eol = memchr(line, '\n', s->hdr_len - at);
        size_t len = eol ? (size_t)(eol - line) + 1 : s->hdr_len - at;
        const char *value;
        size_t value_len;
        if(line_is(line, len, "location:", &value, &value_len) && value_len && (location = malloc(value_len + 1))) {
            memcpy(location, value, value_len);
            location[value_len] = 0;
        }
        at += len;
    }
    s->final_seen = true;
    s->final_len = s->hdr_len;
    CURLcode code = CURLE_OUT_OF_MEMORY;
    CURLU *next = location ? curl_url_dup(r->url) : NULL;
    if(w->redirects >= WS_MAX_REDIRECTS)
        code = CURLE_TOO_MANY_REDIRECTS;
    else if(next)
        code = ws_next_url(next, location);
    free(location);
    struct curl_slist *list = NULL;
    if(code == CURLE_OK) {
        if(same_origin(r->origin, next)) {
            list = r->headers;
        } else {
            if(!r->redirect_headers) {
                for(struct curl_slist *h = r->headers; h && code == CURLE_OK; h = h->next) {
                    if(origin_header(h->data))
                        continue;
                    struct curl_slist *grown = curl_slist_append(r->redirect_headers, h->data);
                    if(grown)
                        r->redirect_headers = grown;
                    else
                        code = CURLE_OUT_OF_MEMORY;
                }
            }
            list = r->redirect_headers;
        }
    }
    if(code == CURLE_OK) {
        struct curl_slist *key = list;
        while(key->next)
            key = key->next;
        if(!ws_key(key->data + 19, w->accept))
            code = CURLE_OUT_OF_MEMORY;
    }
    if(code != CURLE_OK) {
        curl_url_cleanup(next);
        set_message(s, curl_easy_strerror(code));
        finish(c, s, error_class(code), UGO_DETAIL_NONE, code);
        return true;
    }
    curl_off_t total = 0;
    curl_easy_getinfo(s->easy, CURLINFO_TOTAL_TIME_T, &total);
    w->redirect_us += total;
    w->redirects++;
    curl_multi_remove_handle(c->multi, s->easy);
    curl_url_cleanup(r->url);
    r->url = next;
    s->hdr_len = 0;
    s->final_len = 0;
    s->final_seen = false;
    s->has_location = false;
    s->status = 0;
    s->error[0] = 0;
    CURLcode rc = curl_easy_setopt(s->easy, CURLOPT_CURLU, next);
    if(rc == CURLE_OK)
        rc = curl_easy_setopt(s->easy, CURLOPT_HTTPHEADER, list);
    if(rc == CURLE_OK && curl_multi_add_handle(c->multi, s->easy) != CURLM_OK)
        rc = CURLE_FAILED_INIT;
    if(rc != CURLE_OK) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, curl_easy_strerror(rc));
        finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, rc);
    }
    return true;
}

static bool body_header(const char *line)
{
    return header_named(line, "Content-Type") || header_named(line, "Content-Length") ||
           header_named(line, "Content-Encoding") || header_named(line, "Transfer-Encoding");
}

/* curl's CURLFOLLOW_OBEYCODE switches to GET after a 303 but keeps caller-set body headers, so the shim re-issues the
   hop as a new transfer on the same handle; that transfer's credential and custom Host rules are relative to the 303
   target, hence the shim's own origin check. Owner thread. */
static void http_see_other(struct client *c, struct slot *s)
{
    struct req *r = s->req;
    char *target = NULL;
    long hops = 0;
    long connects = 0;
    curl_off_t total = 0;
    curl_easy_getinfo(s->easy, CURLINFO_EFFECTIVE_URL, &target);
    curl_easy_getinfo(s->easy, CURLINFO_REDIRECT_COUNT, &hops);
    curl_easy_getinfo(s->easy, CURLINFO_TOTAL_TIME_T, &total);
    curl_easy_getinfo(s->easy, CURLINFO_NUM_CONNECTS, &connects);
    uint32_t redirects = s->prior_redirects + (uint32_t)hops;
    int64_t spent_us = s->prior_us + total;
    CURLcode code = CURLE_OK;
    if(!r->origin && !(r->origin = curl_url_dup(r->url)))
        code = CURLE_OUT_OF_MEMORY;
    if(code == CURLE_OK && (!target || curl_url_set(r->url, CURLUPART_URL, target, 0) != CURLUE_OK))
        code = CURLE_URL_MALFORMAT;
    int64_t left_ms = r->total_timeout_ms ? (int64_t)r->total_timeout_ms - spent_us / 1000 : 0;
    if(code == CURLE_OK && r->total_timeout_ms && left_ms <= 0)
        code = CURLE_OPERATION_TIMEDOUT;
    struct curl_slist *list = NULL;
    bool same = code == CURLE_OK && same_origin(r->origin, r->url);
    for(struct curl_slist *h = r->headers; code == CURLE_OK && h; h = h->next) {
        if(body_header(h->data) || (!same && origin_header(h->data)))
            continue;
        struct curl_slist *grown = curl_slist_append(list, h->data);
        if(grown)
            list = grown;
        else
            code = CURLE_OUT_OF_MEMORY;
    }
    if(code != CURLE_OK) {
        curl_slist_free_all(list);
        set_message(s, curl_easy_strerror(code));
        finish(c, s, error_class(code), UGO_DETAIL_NONE, code);
        return;
    }
    curl_multi_remove_handle(c->multi, s->easy);
    s->see_other = false;
    s->hdr_len = 0;
    s->final_len = 0;
    s->has_location = false;
    s->status = 0;
    s->body_len = 0;
    s->last_rx = 0;
    s->last_tx = 0;
    s->progress_held = false;
    s->error[0] = 0;
    CURLcode rc = curl_easy_setopt(s->easy, CURLOPT_HTTPHEADER, list);
    if(rc == CURLE_OK) {
        curl_slist_free_all(r->redirect_headers);
        r->redirect_headers = list;
        rc = curl_easy_setopt(s->easy, CURLOPT_CURLU, r->url);
    } else {
        curl_slist_free_all(list);
    }
    if(rc == CURLE_OK)
        rc = curl_easy_setopt(s->easy, CURLOPT_MAXREDIRS, to_long(r->max_redirects - redirects));
    if(rc == CURLE_OK && r->total_timeout_ms)
        rc = curl_easy_setopt(s->easy, CURLOPT_TIMEOUT_MS, to_long((uint64_t)left_ms));
    if(rc == CURLE_OK && !r->head) {
        rc = curl_easy_setopt(s->easy, CURLOPT_CUSTOMREQUEST, NULL);
        if(rc == CURLE_OK)
            rc = curl_easy_setopt(s->easy, CURLOPT_HTTPGET, 1L);
    }
    if(rc == CURLE_OK && curl_multi_add_handle(c->multi, s->easy) != CURLM_OK)
        rc = CURLE_FAILED_INIT;
    if(rc != CURLE_OK) {
        set_error(s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, curl_easy_strerror(rc));
        finish(c, s, UGO_CLASS_INTERNAL, UGO_DETAIL_NONE, rc);
        return;
    }
    s->prior_redirects = redirects;
    s->prior_us = spent_us;
    s->prior_connected = s->prior_connected || connects > 0;
    s->prior_at = c->performs;
}

int32_t ugo_ws_open(ugo_client *client, const ugo_ws_request *request, uint64_t *out_id)
{
    struct client *c = (struct client *)client;
    if(!c || !request || !out_id)
        return UGO_E_INVALID_ARG;
    const ugo_ws_request *q = request;
    if(!q->connect_timeout_ms || !q->close_timeout_ms || !q->max_message_bytes || !q->max_backlog_count ||
       q->max_send_bytes <= 8 || q->max_backlog_bytes < q->max_message_bytes || q->max_backlog_bytes > SIZE_MAX / 2 ||
       !bytes_ok(q->url) || !q->url.len || has_nul(q->url) || !bytes_ok(q->headers))
        return UGO_E_INVALID_ARG;
    uint64_t ws_size = sizeof(struct ws) + (uint64_t)q->max_backlog_count * sizeof(struct ws_msg) +
                       q->max_backlog_bytes + q->max_send_bytes;
    if(ws_size > SIZE_MAX / 2)
        return UGO_E_INVALID_ARG;
    size_t size = sizeof(struct req);
    if(!add_text(&size, q->url) || !add_text(&size, q->headers))
        return UGO_E_INVALID_ARG;

    struct req *r = calloc(1, size);
    if(!r)
        return UGO_E_NOMEM;
    char *cursor = (char *)(r + 1);
    r->body.file = PLAT_NO_FILE;
    r->connect_timeout_ms = q->connect_timeout_ms;
    char *url_text = put_text(&cursor, q->url);
    r->url = curl_url();
    if(!r->url) {
        free_req(r);
        return UGO_E_NOMEM;
    }
    if(!url_ok(r->url, url_text, true)) {
        free_req(r);
        return UGO_E_INVALID_ARG;
    }
    r->origin = curl_url_dup(r->url);
    if(!r->origin) {
        free_req(r);
        return UGO_E_NOMEM;
    }
    int32_t error = parse_headers(q->headers, cursor, &r->headers);
    if(error) {
        free_req(r);
        return error;
    }
    if(has_header(r->headers, "Sec-WebSocket-Key") || has_header(r->headers, "Sec-WebSocket-Extensions")) {
        free_req(r);
        return UGO_E_INVALID_ARG;
    }
    bool failed = false;
    r->headers = add_unless(r->headers, "Accept", "Accept:", &failed);
    struct ws *w = failed ? NULL : calloc(1, (size_t)ws_size);
    char line[19 + 25] = "Sec-WebSocket-Key: ";
    if(w && ws_key(line + 19, w->accept)) {
        struct curl_slist *grown = curl_slist_append(r->headers, line);
        failed = !grown;
        if(grown)
            r->headers = grown;
    } else {
        failed = true;
    }
    if(failed) {
        free_req(r);
        free(w);
        return UGO_E_NOMEM;
    }
    w->backlog_cap = q->max_backlog_bytes;
    w->count_cap = q->max_backlog_count;
    w->send_cap = q->max_send_bytes;
    w->max_message = q->max_message_bytes;
    w->close_timeout_ms = q->close_timeout_ms;
    w->msgs = (struct ws_msg *)(w + 1);
    w->backlog = (uint8_t *)(w->msgs + w->count_cap);
    w->send = w->backlog + w->backlog_cap;
    w->fd = CURL_SOCKET_BAD;
    /* curl stops applying CURLOPT_TIMEOUT_MS to a CONNECT_ONLY transfer once connected. */
    if(q->open_timeout_ms)
        w->deadline_us = plat_now_us() + (int64_t)q->open_timeout_ms * 1000;
    return claim_slot(c, r, w, NULL, false, out_id);
}

/* Caller holds client->lock. */
static int32_t ws_lookup(struct client *c, uint64_t id, struct slot **out)
{
    struct slot *s = lookup(c, id);
    if(!s)
        return UGO_E_STALE_ID;
    if(!s->ws)
        return UGO_E_INVALID_ARG;
    *out = s;
    return UGO_OK;
}

int32_t ugo_ws_send(ugo_client *client, uint64_t ws_id, uint32_t type, const uint8_t *data, uint32_t len)
{
    struct client *c = (struct client *)client;
    if(!c || (!data && len) || (type != UGO_WS_TEXT && type != UGO_WS_BINARY) ||
       (type == UGO_WS_TEXT && !utf8_valid(data, len)))
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = NULL;
    int32_t result = c->shutting_down ? UGO_E_SHUTDOWN : ws_lookup(c, ws_id, &s);
    struct ws *w = s ? s->ws : NULL;
    if(result == UGO_OK && (uint64_t)len + 8 > w->send_cap)
        result = UGO_E_INVALID_ARG;
    else if(result == UGO_OK && (s->state != SLOT_ACTIVE || w->state != WS_OPEN || s->abort))
        result = UGO_E_STATE;
    else if(result == UGO_OK && w->send_cap - w->send_used < len + 8)
        result = UGO_E_CAPACITY;
    if(result == UGO_OK) {
        uint64_t header = (uint64_t)type << 32 | len;
        uint32_t tail = (uint32_t)(((uint64_t)w->send_head + w->send_used) % w->send_cap);
        const uint8_t *parts[2] = {(const uint8_t *)&header, data};
        uint32_t sizes[2] = {8, len};
        for(int i = 0; i < 2; ++i) {
            uint32_t first = w->send_cap - tail < sizes[i] ? w->send_cap - tail : sizes[i];
            if(first)
                memcpy(w->send + tail, parts[i], first);
            if(sizes[i] > first)
                memcpy(w->send, parts[i] + first, sizes[i] - first);
            tail = (uint32_t)(((uint64_t)tail + sizes[i]) % w->send_cap);
        }
        w->send_used += len + 8;
        queue_owner(c, s);
    }
    plat_mutex_unlock(&c->lock);
    return result;
}

int32_t ugo_ws_close(ugo_client *client, uint64_t ws_id, uint32_t code, const uint8_t *reason, uint32_t reason_len)
{
    struct client *c = (struct client *)client;
    if(!c || !close_code_ok(code) || reason_len > UGO_WS_CLOSE_REASON_MAX || (!reason && reason_len) ||
       (reason_len && !utf8_valid(reason, reason_len)))
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = NULL;
    int32_t result = c->shutting_down ? UGO_E_SHUTDOWN : ws_lookup(c, ws_id, &s);
    struct ws *w = s ? s->ws : NULL;
    if(result == UGO_OK && (s->state != SLOT_ACTIVE || w->state != WS_OPEN || s->abort))
        result = UGO_E_STATE;
    if(result == UGO_OK) {
        w->close_out[0] = (uint8_t)(code >> 8);
        w->close_out[1] = (uint8_t)code;
        if(reason_len)
            memcpy(w->close_out + 2, reason, reason_len);
        w->close_out_len = reason_len + 2;
        w->state = WS_CLOSING;
        w->deadline_us = plat_now_us() + (int64_t)w->close_timeout_ms * 1000;
        queue_owner(c, s);
    }
    plat_mutex_unlock(&c->lock);
    return result;
}

int32_t ugo_ws_release(ugo_client *client, uint64_t ws_id, uint64_t through_seq)
{
    struct client *c = (struct client *)client;
    if(!c)
        return UGO_E_INVALID_ARG;
    plat_mutex_lock(&c->lock);
    struct slot *s = NULL;
    int32_t result = ws_lookup(c, ws_id, &s);
    struct ws *w = s ? s->ws : NULL;
    if(result == UGO_OK && through_seq > w->drained)
        result = UGO_E_INVALID_ARG;
    while(result == UGO_OK && w->released < through_seq) {
        w->unreleased_bytes -= w->msgs[w->msg_head].len;
        w->msg_head = (w->msg_head + 1) % w->count_cap;
        w->released++;
    }
    if(result == UGO_OK && w->rx_paused) {
        w->rx_paused = false;
        wake_owner(c);
    }
    plat_mutex_unlock(&c->lock);
    return result;
}
