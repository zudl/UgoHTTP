#ifndef UGO_H
#define UGO_H

#include <stdint.h>

#if defined(UGO_BUILD_SHARED)
#  if defined(_WIN32)
#    define UGO_API __declspec(dllexport)
#  else
#    define UGO_API __attribute__((visibility("default")))
#  endif
#else
#  define UGO_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Every struct is blittable with one layout on every ABI, 32-bit included:
 * addresses are carried as uint64_t and every 8-byte field sits at an 8-byte
 * offset, so the layout does not depend on how a compiler or runtime aligns
 * 64-bit integers.
 */

#define UGO_ABI_VERSION 2u

#define UGO_INVALID_ID 0u
#define UGO_WS_CLOSE_REASON_MAX 123u
#define UGO_METHOD_MAX 32u
#define UGO_ERROR_BODY_MAX 65536u

typedef struct ugo_client ugo_client;

enum {
    UGO_OK = 0,
    UGO_E_INVALID_ARG = -1,
    UGO_E_STALE_ID = -2,
    UGO_E_STATE = -3,
    UGO_E_CAPACITY = -4,
    UGO_E_BUSY = -5,
    UGO_E_SHUTDOWN = -6,
    UGO_E_ABI = -7,
    UGO_E_NOMEM = -8
};

enum {
    UGO_CLASS_NONE = 0,
    UGO_CLASS_DNS = 1,
    UGO_CLASS_CONNECT = 2,
    UGO_CLASS_TLS = 3,
    UGO_CLASS_TIMEOUT = 4,
    UGO_CLASS_TRANSFER = 5,
    UGO_CLASS_HTTP_STATUS = 6,
    UGO_CLASS_ABORTED = 7,
    UGO_CLASS_LOCAL_IO = 8,
    UGO_CLASS_INTERNAL = 9
};

/* Narrows the class where the caller's policy depends on the cause. */
enum {
    UGO_DETAIL_NONE = 0,
    UGO_DETAIL_CALLER = 1,
    UGO_DETAIL_SHUTDOWN = 2,
    UGO_DETAIL_BACKGROUND_EXPIRED = 3,
    UGO_DETAIL_MEMORY_LIMIT = 4,
    UGO_DETAIL_WS_HANDSHAKE = 5,
    UGO_DETAIL_WS_CLOSED_BY_LOCAL = 6,
    UGO_DETAIL_WS_CLOSED_BY_REMOTE = 7,
    UGO_DETAIL_RESUME_MISMATCH = 8
};

enum {
    UGO_BODY_NONE = 0,
    UGO_BODY_MEMORY = 1,
    UGO_BODY_FILE = 2,
    UGO_BODY_MULTIPART = 3
};

enum {
    UGO_PART_MEMORY = 0,
    UGO_PART_FILE = 1
};

enum {
    UGO_REQ_KEEP_PARTIAL = 1 << 0,
    UGO_REQ_DETACHED = 1 << 1,
    UGO_REQ_FINISH_IN_BACKGROUND = 1 << 2,
    UGO_REQ_RESUME = 1 << 3
};

/* ugo_client_config.flags; a release build treats it as an unknown bit. */
enum {
    UGO_CLIENT_TLS_INSECURE_DEV = 1 << 0
};

enum {
    UGO_HTTP_NONE = 0,
    UGO_HTTP_1_0 = 10,
    UGO_HTTP_1_1 = 11,
    UGO_HTTP_2 = 20
};

enum {
    UGO_EV_HEADERS = 1,
    UGO_EV_PROGRESS = 2,
    UGO_EV_COMPLETED = 3,
    UGO_EV_WS_MESSAGE = 4
};

/* The RFC 6455 data frame opcodes. */
enum {
    UGO_WS_TEXT = 1,
    UGO_WS_BINARY = 2
};

/* Byte view; text is UTF-8 and never NUL-terminated. */
typedef struct ugo_bytes {
    uint64_t ptr;
    uint64_t len;
} ugo_bytes;

typedef struct ugo_client_config {
    uint32_t abi_version;
    uint32_t max_transfers;
    uint32_t max_total_connections;
    uint32_t dns_cache_ttl_s;
    uint32_t flags;
    uint32_t reserved;
    ugo_bytes ca_bundle;
} ugo_client_config;

typedef struct ugo_mime_part {
    ugo_bytes name;
    ugo_bytes filename;
    ugo_bytes content_type;
    ugo_bytes data;
    uint32_t source;
    uint32_t reserved;
} ugo_mime_part;

typedef struct ugo_request {
    ugo_bytes method;
    ugo_bytes url;
    ugo_bytes headers;
    ugo_bytes body;
    uint64_t parts;
    uint32_t part_count;
    uint32_t body_kind;
    ugo_bytes sink_path;
    uint64_t memory_limit;
    uint64_t max_recv_bytes_per_s;
    uint64_t max_send_bytes_per_s;
    uint32_t low_speed_bytes_per_s;
    uint32_t low_speed_time_s;
    uint32_t connect_timeout_ms;
    uint32_t total_timeout_ms;
    uint32_t max_redirects;
    uint32_t flags;
} ugo_request;

typedef struct ugo_ws_request {
    ugo_bytes url;
    ugo_bytes headers;
    uint32_t connect_timeout_ms;
    uint32_t open_timeout_ms;
    uint32_t close_timeout_ms;
    uint32_t max_message_bytes;
    uint64_t max_backlog_bytes;
    uint32_t max_backlog_count;
    uint32_t max_send_bytes;
} ugo_ws_request;

typedef struct ugo_result {
    ugo_bytes body;
    ugo_bytes headers;
    ugo_bytes error_message;
} ugo_result;

typedef struct ugo_ev_headers {
    uint32_t http_status;
    uint32_t http_version;
    int64_t content_length;
    ugo_bytes headers;
    uint64_t resume_offset;
} ugo_ev_headers;

typedef struct ugo_ev_progress {
    uint64_t received;
    int64_t receive_total;
    uint64_t sent;
    int64_t send_total;
} ugo_ev_progress;

typedef struct ugo_ev_completed {
    uint32_t error_class;
    int32_t curl_code;
    uint32_t http_status;
    uint32_t http_version;
    uint64_t bytes_received;
    uint64_t bytes_sent;
    uint32_t connection_reused;
    uint32_t redirect_count;
    uint32_t detail;
    uint32_t ws_close_code;
    int64_t queue_us;
    int64_t namelookup_us;
    int64_t connect_us;
    int64_t appconnect_us;
    int64_t starttransfer_us;
    int64_t total_us;
    int64_t redirect_us;
} ugo_ev_completed;

typedef struct ugo_ev_ws_message {
    uint64_t seq;
    ugo_bytes data;
    uint32_t type;
    uint32_t reserved;
} ugo_ev_ws_message;

typedef struct ugo_event {
    uint32_t kind;
    uint32_t reserved;
    uint64_t id;
    int64_t time_us;
    union {
        ugo_ev_headers headers;
        ugo_ev_progress progress;
        ugo_ev_completed completed;
        ugo_ev_ws_message ws_message;
        uint64_t payload[13];
    } u;
} ugo_event;

UGO_API int64_t ugo_monotonic_us(void);

UGO_API int32_t ugo_client_create(const ugo_client_config* config, ugo_client** out_client);
UGO_API int32_t ugo_client_shutdown(ugo_client* client);
UGO_API void ugo_client_destroy(ugo_client* client);
UGO_API int32_t ugo_shutdown_all(void);

UGO_API int32_t ugo_submit(ugo_client* client, const ugo_request* request, uint64_t* out_id);
UGO_API int32_t ugo_abort(ugo_client* client, uint64_t id);
UGO_API int32_t ugo_release(ugo_client* client, uint64_t id);
UGO_API int32_t ugo_get_result(ugo_client* client, uint64_t id, ugo_result* out_result);

UGO_API int32_t ugo_wait_events(ugo_client* client, ugo_event* events, uint32_t capacity, uint32_t timeout_ms);

UGO_API int32_t ugo_ws_open(ugo_client* client, const ugo_ws_request* request, uint64_t* out_id);
UGO_API int32_t ugo_ws_send(ugo_client* client, uint64_t ws_id, uint32_t type, const uint8_t* data, uint32_t len);
UGO_API int32_t ugo_ws_close(ugo_client* client, uint64_t ws_id, uint32_t code, const uint8_t* reason, uint32_t reason_len);
UGO_API int32_t ugo_ws_release(ugo_client* client, uint64_t ws_id, uint64_t through_seq);

#ifdef __cplusplus
}
#endif

#endif
