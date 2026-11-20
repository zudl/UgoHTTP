/* Loads ugo.dll the way a P/Invoke caller does and runs requests through its exports only. */
#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "ugo.h"

#define EXPORTS(X) X(ugo_monotonic_us) X(ugo_client_create) X(ugo_client_shutdown) X(ugo_client_destroy) \
    X(ugo_shutdown_all) X(ugo_submit) X(ugo_abort) X(ugo_release) X(ugo_get_result) X(ugo_wait_events) \
    X(ugo_ws_open) X(ugo_ws_send) X(ugo_ws_close) X(ugo_ws_release)
#define DECLARE(name) static __typeof__(&name) p_##name;
EXPORTS(DECLARE)

static SOCKET listener = INVALID_SOCKET;

static DWORD WINAPI serve(LPVOID unused)
{
    (void)unused;
    static const char reply[] = "HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\nugo";
    for(;;) {
        SOCKET s = accept(listener, NULL, NULL);
        if(s == INVALID_SOCKET)
            return 0;
        char request[4096];
        recv(s, request, sizeof(request), 0);
        send(s, reply, (int)sizeof(reply) - 1, 0);
        shutdown(s, SD_SEND);
        closesocket(s);
    }
}

static int start_server(void)
{
    WSADATA wsa;
    if(WSAStartup(MAKEWORD(2, 2), &wsa))
        return -1;
    listener = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    int len = sizeof(addr);
    if(listener == INVALID_SOCKET || bind(listener, (struct sockaddr *)&addr, sizeof(addr)) ||
       listen(listener, 8) || getsockname(listener, (struct sockaddr *)&addr, &len))
        return -1;
    HANDLE thread = CreateThread(NULL, 0, serve, NULL, 0, NULL);
    if(!thread)
        return -1;
    CloseHandle(thread);
    return ntohs(addr.sin_port);
}

static ugo_bytes text(const char *s)
{
    ugo_bytes b = {(uint64_t)(uintptr_t)s, strlen(s)};
    return b;
}

/* Prints one JSON object; returns the error class, or -1 if the API misbehaved, a success is not a 200,
 * an https success is not HTTP/2 or its body differs from `body` (NULL: any). */
static int request(ugo_client *client, const char *url, const char *body)
{
    ugo_request r;
    memset(&r, 0, sizeof(r));
    r.method = text("GET");
    r.url = text(url);
    r.memory_limit = 1 << 20;
    r.connect_timeout_ms = 15000;
    r.total_timeout_ms = 30000;
    r.max_redirects = 5;
    uint64_t id = 0;
    int32_t rc = p_ugo_submit(client, &r, &id);
    if(rc != UGO_OK) {
        printf("{\"url\":\"%s\",\"submit\":%d}", url, (int)rc);
        return -1;
    }
    ugo_event events[8];
    ugo_ev_completed done;
    int completed = 0;
    int64_t deadline = p_ugo_monotonic_us() + 60 * 1000000LL;
    while(!completed && p_ugo_monotonic_us() < deadline) {
        int32_t n = p_ugo_wait_events(client, events, 8, 1000);
        for(int32_t i = 0; i < n; ++i) {
            if(events[i].id == id && events[i].kind == UGO_EV_COMPLETED) {
                done = events[i].u.completed;
                completed = 1;
            }
        }
    }
    if(!completed) {
        printf("{\"url\":\"%s\",\"completed\":false}", url);
        return -1;
    }
    ugo_result result;
    if(p_ugo_get_result(client, id, &result) != UGO_OK)
        return -1;
    printf("{\"url\":\"%s\",\"error_class\":%u,\"curl_code\":%d,\"http_status\":%u,\"http_version\":%u,"
           "\"body_bytes\":%llu}", url, done.error_class, done.curl_code, done.http_status, done.http_version,
           (unsigned long long)result.body.len);
    int mismatch = done.error_class == UGO_CLASS_NONE &&
        (done.http_status != 200 || (!strncmp(url, "https:", 6) && done.http_version != UGO_HTTP_2) ||
         (body && (result.body.len != strlen(body) ||
                   memcmp((const void *)(uintptr_t)result.body.ptr, body, strlen(body)))));
    if(p_ugo_release(client, id) != UGO_OK || mismatch)
        return -1;
    return (int)done.error_class;
}

/* usage: ugo-dll-probe <ugo.dll> [<url> <expected error class>]...
 * Without URLs: plain HTTP to an in-process server via localhost (threaded resolver), and a TLS handshake
 * against that plain server, which must fail as TLS after mbedTLS has started. */
int main(int argc, char **argv)
{
    if(argc < 2 || argc % 2)
        return 2;
    HMODULE dll = LoadLibraryA(argv[1]);
    if(!dll) {
        fprintf(stderr, "LoadLibrary failed: %lu\n", GetLastError());
        return 2;
    }
    int missing = 0, resolved = 0;
#define RESOLVE(name) \
    if((p_##name = (__typeof__(&name))(void (*)(void))GetProcAddress(dll, #name))) { \
        resolved++; \
    } else { \
        fprintf(stderr, "missing export %s\n", #name); \
        missing = 1; \
    }
    EXPORTS(RESOLVE)
    if(missing)
        return 2;
    ugo_client_config config = {0};
    config.abi_version = UGO_ABI_VERSION;
    config.max_transfers = 4;
    ugo_client *client = NULL;
    if(p_ugo_client_create(&config, &client) != UGO_OK)
        return 2;
    char local[2][64];
    const char *urls[2];
    int expected[2];
    int count = 0;
    if(argc == 2) {
        int port = start_server();
        if(port < 0)
            return 2;
        snprintf(local[0], sizeof(local[0]), "http://localhost:%d/probe", port);
        snprintf(local[1], sizeof(local[1]), "https://localhost:%d/probe", port);
        urls[0] = local[0];
        urls[1] = local[1];
        expected[0] = UGO_CLASS_NONE;
        expected[1] = UGO_CLASS_TLS;
        count = 2;
    }
    int failed = 0;
    printf("{\"exports\":%d,\"requests\":[", resolved);
    for(int i = 0; i < count + (argc - 2) / 2; ++i) {
        const char *url = i < count ? urls[i] : argv[2 + 2 * (i - count)];
        int want = i < count ? expected[i] : atoi(argv[3 + 2 * (i - count)]);
        if(i)
            printf(",");
        if(request(client, url, i < count ? "ugo" : NULL) != want)
            failed = 1;
    }
    printf("]}\n");
    p_ugo_client_destroy(client);
    return failed;
}
