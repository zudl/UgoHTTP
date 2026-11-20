#include <stddef.h>

#include "ugo.h"

/* Rows in declaration order: UgoAbi.Dump() emits the same lines from reflection. */
#define UGO_LAYOUT(SIZE, FIELD) \
    SIZE(ugo_bytes, 16) \
    FIELD(ugo_bytes, ptr, 0) \
    FIELD(ugo_bytes, len, 8) \
    SIZE(ugo_client_config, 40) \
    FIELD(ugo_client_config, abi_version, 0) \
    FIELD(ugo_client_config, max_transfers, 4) \
    FIELD(ugo_client_config, max_total_connections, 8) \
    FIELD(ugo_client_config, dns_cache_ttl_s, 12) \
    FIELD(ugo_client_config, flags, 16) \
    FIELD(ugo_client_config, reserved, 20) \
    FIELD(ugo_client_config, ca_bundle, 24) \
    SIZE(ugo_mime_part, 72) \
    FIELD(ugo_mime_part, name, 0) \
    FIELD(ugo_mime_part, filename, 16) \
    FIELD(ugo_mime_part, content_type, 32) \
    FIELD(ugo_mime_part, data, 48) \
    FIELD(ugo_mime_part, source, 64) \
    FIELD(ugo_mime_part, reserved, 68) \
    SIZE(ugo_request, 144) \
    FIELD(ugo_request, method, 0) \
    FIELD(ugo_request, url, 16) \
    FIELD(ugo_request, headers, 32) \
    FIELD(ugo_request, body, 48) \
    FIELD(ugo_request, parts, 64) \
    FIELD(ugo_request, part_count, 72) \
    FIELD(ugo_request, body_kind, 76) \
    FIELD(ugo_request, sink_path, 80) \
    FIELD(ugo_request, memory_limit, 96) \
    FIELD(ugo_request, max_recv_bytes_per_s, 104) \
    FIELD(ugo_request, max_send_bytes_per_s, 112) \
    FIELD(ugo_request, low_speed_bytes_per_s, 120) \
    FIELD(ugo_request, low_speed_time_s, 124) \
    FIELD(ugo_request, connect_timeout_ms, 128) \
    FIELD(ugo_request, total_timeout_ms, 132) \
    FIELD(ugo_request, max_redirects, 136) \
    FIELD(ugo_request, flags, 140) \
    SIZE(ugo_ws_request, 64) \
    FIELD(ugo_ws_request, url, 0) \
    FIELD(ugo_ws_request, headers, 16) \
    FIELD(ugo_ws_request, connect_timeout_ms, 32) \
    FIELD(ugo_ws_request, open_timeout_ms, 36) \
    FIELD(ugo_ws_request, close_timeout_ms, 40) \
    FIELD(ugo_ws_request, max_message_bytes, 44) \
    FIELD(ugo_ws_request, max_backlog_bytes, 48) \
    FIELD(ugo_ws_request, max_backlog_count, 56) \
    FIELD(ugo_ws_request, max_send_bytes, 60) \
    SIZE(ugo_result, 48) \
    FIELD(ugo_result, body, 0) \
    FIELD(ugo_result, headers, 16) \
    FIELD(ugo_result, error_message, 32) \
    SIZE(ugo_ev_headers, 40) \
    FIELD(ugo_ev_headers, http_status, 0) \
    FIELD(ugo_ev_headers, http_version, 4) \
    FIELD(ugo_ev_headers, content_length, 8) \
    FIELD(ugo_ev_headers, headers, 16) \
    FIELD(ugo_ev_headers, resume_offset, 32) \
    SIZE(ugo_ev_progress, 32) \
    FIELD(ugo_ev_progress, received, 0) \
    FIELD(ugo_ev_progress, receive_total, 8) \
    FIELD(ugo_ev_progress, sent, 16) \
    FIELD(ugo_ev_progress, send_total, 24) \
    SIZE(ugo_ev_completed, 104) \
    FIELD(ugo_ev_completed, error_class, 0) \
    FIELD(ugo_ev_completed, curl_code, 4) \
    FIELD(ugo_ev_completed, http_status, 8) \
    FIELD(ugo_ev_completed, http_version, 12) \
    FIELD(ugo_ev_completed, bytes_received, 16) \
    FIELD(ugo_ev_completed, bytes_sent, 24) \
    FIELD(ugo_ev_completed, connection_reused, 32) \
    FIELD(ugo_ev_completed, redirect_count, 36) \
    FIELD(ugo_ev_completed, detail, 40) \
    FIELD(ugo_ev_completed, ws_close_code, 44) \
    FIELD(ugo_ev_completed, queue_us, 48) \
    FIELD(ugo_ev_completed, namelookup_us, 56) \
    FIELD(ugo_ev_completed, connect_us, 64) \
    FIELD(ugo_ev_completed, appconnect_us, 72) \
    FIELD(ugo_ev_completed, starttransfer_us, 80) \
    FIELD(ugo_ev_completed, total_us, 88) \
    FIELD(ugo_ev_completed, redirect_us, 96) \
    SIZE(ugo_ev_ws_message, 32) \
    FIELD(ugo_ev_ws_message, seq, 0) \
    FIELD(ugo_ev_ws_message, data, 8) \
    FIELD(ugo_ev_ws_message, type, 24) \
    FIELD(ugo_ev_ws_message, reserved, 28) \
    SIZE(ugo_event, 128) \
    FIELD(ugo_event, kind, 0) \
    FIELD(ugo_event, reserved, 4) \
    FIELD(ugo_event, id, 8) \
    FIELD(ugo_event, time_us, 16) \
    FIELD(ugo_event, u, 24)

/* Same names and order as the const fields of UgoAbi. */
#define UGO_CONSTANTS(C) \
    C(UGO_ABI_VERSION) C(UGO_INVALID_ID) C(UGO_WS_CLOSE_REASON_MAX) C(UGO_METHOD_MAX) C(UGO_ERROR_BODY_MAX) \
    C(UGO_OK) C(UGO_E_INVALID_ARG) C(UGO_E_STALE_ID) C(UGO_E_STATE) C(UGO_E_CAPACITY) \
    C(UGO_E_BUSY) C(UGO_E_SHUTDOWN) C(UGO_E_ABI) C(UGO_E_NOMEM) \
    C(UGO_CLASS_NONE) C(UGO_CLASS_DNS) C(UGO_CLASS_CONNECT) C(UGO_CLASS_TLS) C(UGO_CLASS_TIMEOUT) \
    C(UGO_CLASS_TRANSFER) C(UGO_CLASS_HTTP_STATUS) C(UGO_CLASS_ABORTED) C(UGO_CLASS_LOCAL_IO) \
    C(UGO_CLASS_INTERNAL) \
    C(UGO_DETAIL_NONE) C(UGO_DETAIL_CALLER) C(UGO_DETAIL_SHUTDOWN) C(UGO_DETAIL_BACKGROUND_EXPIRED) \
    C(UGO_DETAIL_MEMORY_LIMIT) C(UGO_DETAIL_WS_HANDSHAKE) C(UGO_DETAIL_WS_CLOSED_BY_LOCAL) \
    C(UGO_DETAIL_WS_CLOSED_BY_REMOTE) C(UGO_DETAIL_RESUME_MISMATCH) \
    C(UGO_BODY_NONE) C(UGO_BODY_MEMORY) C(UGO_BODY_FILE) C(UGO_BODY_MULTIPART) \
    C(UGO_PART_MEMORY) C(UGO_PART_FILE) \
    C(UGO_REQ_KEEP_PARTIAL) C(UGO_REQ_DETACHED) C(UGO_REQ_FINISH_IN_BACKGROUND) C(UGO_REQ_RESUME) \
    C(UGO_CLIENT_TLS_INSECURE_DEV) \
    C(UGO_HTTP_NONE) C(UGO_HTTP_1_0) C(UGO_HTTP_1_1) C(UGO_HTTP_2) \
    C(UGO_EV_HEADERS) C(UGO_EV_PROGRESS) C(UGO_EV_COMPLETED) C(UGO_EV_WS_MESSAGE) \
    C(UGO_WS_TEXT) C(UGO_WS_BINARY)

#define UGO_CHECK_SIZE(T, N) _Static_assert(sizeof(T) == (N), "sizeof " #T);
#define UGO_CHECK_FIELD(T, F, N) _Static_assert(offsetof(T, F) == (N), "offsetof " #T "." #F);
UGO_LAYOUT(UGO_CHECK_SIZE, UGO_CHECK_FIELD)

_Static_assert(sizeof(((ugo_event*)0)->u) == sizeof(((ugo_event*)0)->u.payload), "payload covers the union");

#ifdef UGO_LAYOUT_PRINT
#include <stdio.h>

#define UGO_PRINT_SIZE(T, N) printf("%s %u\n", #T, (unsigned)sizeof(T));
#define UGO_PRINT_FIELD(T, F, N) printf("%s.%s %u\n", #T, #F, (unsigned)offsetof(T, F));
#define UGO_PRINT_CONSTANT(K) printf("%s %lld\n", #K, (long long)(K));

int main(void)
{
    UGO_LAYOUT(UGO_PRINT_SIZE, UGO_PRINT_FIELD)
    UGO_CONSTANTS(UGO_PRINT_CONSTANT)
    return 0;
}
#else
typedef int ugo_layout_check_nonempty;
#endif
