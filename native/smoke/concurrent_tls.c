#include <curl/curl.h>
#include <mbedtls/ssl.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>

struct client {
    const char *url;
    const char *ca;
    pthread_barrier_t *start;
    int completed;
    int failed;
};

static size_t discard(char *data, size_t size, size_t count, void *context)
{
    (void)data;
    size_t *received = context;
    *received += size * count;
    return size * count;
}

static void *perform(void *argument)
{
    struct client *client = argument;
    CURLM *multi = curl_multi_init();
    if(!multi)
        abort();
    pthread_barrier_wait(client->start);
    for(int i = 0; i < 40; ++i) {
        CURL *easy = curl_easy_init();
        if(!easy)
            abort();
        size_t received = 0;
        long tls = i % 2 ? CURL_SSLVERSION_TLSv1_2 | CURL_SSLVERSION_MAX_TLSv1_2 :
                           CURL_SSLVERSION_TLSv1_3 | CURL_SSLVERSION_MAX_TLSv1_3;
#define SETOPT(option, value) do { if(curl_easy_setopt(easy, option, value) != CURLE_OK) abort(); } while(0)
        SETOPT(CURLOPT_URL, client->url);
        SETOPT(CURLOPT_CAINFO, client->ca);
        SETOPT(CURLOPT_SSL_VERIFYPEER, 1L);
        SETOPT(CURLOPT_SSL_VERIFYHOST, 2L);
        SETOPT(CURLOPT_SSLVERSION, tls);
        SETOPT(CURLOPT_NOSIGNAL, 1L);
        SETOPT(CURLOPT_TIMEOUT_MS, 10000L);
        SETOPT(CURLOPT_FORBID_REUSE, 1L);
        SETOPT(CURLOPT_WRITEFUNCTION, discard);
        SETOPT(CURLOPT_WRITEDATA, &received);
#undef SETOPT
        if(curl_multi_add_handle(multi, easy) != CURLM_OK)
            abort();
        int active = 0;
        CURLMcode code = curl_multi_perform(multi, &active);
        while(code == CURLM_OK && active) {
            code = curl_multi_poll(multi, NULL, 0, 1000, NULL);
            if(code == CURLM_OK)
                code = curl_multi_perform(multi, &active);
        }
        int remaining;
        CURLMsg *message = curl_multi_info_read(multi, &remaining);
        long status = 0;
        curl_easy_getinfo(easy, CURLINFO_RESPONSE_CODE, &status);
        if(code != CURLM_OK || !message || message->msg != CURLMSG_DONE ||
           message->data.result != CURLE_OK || status != 200 || received != sizeof("UgoHTTP fault bench\n") - 1) {
            fprintf(stderr, "TLS client failed: multi=%d curl=%d status=%ld bytes=%zu\n",
                    (int)code, message ? (int)message->data.result : -1, status, received);
            client->failed = 1;
        }
        else
            ++client->completed;
        curl_multi_remove_handle(multi, easy);
        curl_easy_cleanup(easy);
        if(client->failed)
            break;
    }
    curl_multi_cleanup(multi);
    return NULL;
}

int main(int argc, char **argv)
{
    if(argc != 3 || curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK)
        return 2;
    /* mbedTLS lazily builds this process-wide list without a mutex. */
    mbedtls_ssl_list_ciphersuites();
    pthread_barrier_t start;
    if(pthread_barrier_init(&start, NULL, 2))
        return 2;
    struct client clients[2] = {{argv[1], argv[2], &start, 0, 0},
                                {argv[1], argv[2], &start, 0, 0}};
    pthread_t threads[2];
    for(int i = 0; i < 2; ++i)
        if(pthread_create(&threads[i], NULL, perform, &clients[i]))
            abort();
    for(int i = 0; i < 2; ++i)
        if(pthread_join(threads[i], NULL))
            abort();
    pthread_barrier_destroy(&start);
    curl_global_cleanup();
    int failed = clients[0].failed || clients[1].failed;
    printf("{\"pass\":%s,\"clients\":2,\"curlm_per_thread\":1,\"tls_ciphersuites_warmed\":true,\"tls_versions\":[\"1.2\",\"1.3\"],\"completed\":[%d,%d]}\n",
           failed ? "false" : "true", clients[0].completed, clients[1].completed);
    return failed;
}
