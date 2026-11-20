#include <curl/curl.h>
#include <mbedtls/version.h>
#include <nghttp2/nghttp2.h>
#include <stdio.h>
#include "ca_bundle.h"

#if !defined(MBEDTLS_THREADING_C) || (!defined(MBEDTLS_THREADING_PTHREAD) && !defined(_WIN32)) || \
    (!defined(MBEDTLS_THREADING_ALT) && defined(_WIN32))
#error TLS requires thread support: pthread, or the Windows primitives
#endif
#if defined(MBEDTLS_SSL_SRV_C) || defined(MBEDTLS_SSL_PROTO_DTLS)
#error Only TLS clients are supported
#endif
#if !defined(MBEDTLS_SSL_PROTO_TLS1_2) || !defined(MBEDTLS_SSL_PROTO_TLS1_3)
#error Both TLS 1.2 and TLS 1.3 are required
#endif

static size_t discard(char *ptr, size_t size, size_t count, void *context)
{
    (void)ptr;
    (void)context;
    return size * count;
}

#if defined(_WIN32)
void plat_tls_init(void);
#endif

int main(int argc, char **argv)
{
#if defined(_WIN32)
    plat_tls_init();
#endif
    if(curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK)
        return 2;
    const curl_version_info_data *v = curl_version_info(CURLVERSION_NOW);
    const char *mbed_version = mbedtls_version_get_string();
    printf("{\"curl\":\"%s\",\"ssl\":\"%s\",\"mbedtls\":\"%s\","
           "\"nghttp2\":\"%s\",\"zlib\":\"%s\",\"protocols\":[", v->version,
           v->ssl_version ? v->ssl_version : "", mbed_version,
           nghttp2_version(0)->version_str, v->libz_version ? v->libz_version : "");
    for(size_t i = 0; v->protocols[i]; ++i)
        printf("%s\"%s\"", i ? "," : "", v->protocols[i]);
    printf("],\"features\":[");
    for(size_t i = 0; v->feature_names && v->feature_names[i]; ++i)
        printf("%s\"%s\"", i ? "," : "", v->feature_names[i]);
    printf("]");
    int result = 0;
    if(argc == 2 || argc == 3) {
        CURL *easy = curl_easy_init();
        if(!easy)
            return 2;
        struct curl_blob ca = {(void *)ugo_ca_bundle, sizeof(ugo_ca_bundle) - 1,
                               CURL_BLOB_NOCOPY};
        static char replacement_ca[256 * 1024];
        if(argc == 3) {
            FILE *file = fopen(argv[2], "rb");
            if(!file)
                return 2;
            size_t length = fread(replacement_ca, 1, sizeof(replacement_ca), file);
            int extra = fgetc(file);
            int failed = ferror(file);
            fclose(file);
            if(failed || extra != EOF || length == 0)
                return 2;
            ca.data = replacement_ca;
            ca.len = length;
        }
        CURLcode code;
        char error[CURL_ERROR_SIZE] = {0};
#define SETOPT(option, value) do { \
    code = curl_easy_setopt(easy, option, value); \
    if(code != CURLE_OK) { curl_easy_cleanup(easy); curl_global_cleanup(); return 2; } \
} while(0)
        SETOPT(CURLOPT_ERRORBUFFER, error);
        SETOPT(CURLOPT_URL, argv[1]);
        SETOPT(CURLOPT_CAINFO_BLOB, &ca);
        SETOPT(CURLOPT_SSL_VERIFYPEER, 1L);
        SETOPT(CURLOPT_SSL_VERIFYHOST, 2L);
        SETOPT(CURLOPT_NOSIGNAL, 1L);
        SETOPT(CURLOPT_CONNECTTIMEOUT_MS, 15000L);
        SETOPT(CURLOPT_TIMEOUT_MS, 30000L);
        SETOPT(CURLOPT_WRITEFUNCTION, discard);
        code = curl_easy_perform(easy);
        if(code != CURLE_OK)
            fprintf(stderr, "%s: %s\n", curl_easy_strerror(code), error);
        long status = 0;
        long verify_result = -1;
        curl_easy_getinfo(easy, CURLINFO_RESPONSE_CODE, &status);
        curl_easy_getinfo(easy, CURLINFO_SSL_VERIFYRESULT, &verify_result);
        printf(",\"http_status\":%ld,\"curl_code\":%d,\"verify_result\":%ld,"
               "\"peer_verification\":true,\"hostname_verification\":true",
               status, (int)code, verify_result);
        result = code == CURLE_OK ? 0 : (int)code;
        curl_easy_cleanup(easy);
    }
    puts("}");
    curl_global_cleanup();
    return result;
}
