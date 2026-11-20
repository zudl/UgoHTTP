/* No include guard: TLS and PSA apply this after different default headers. */

#define MBEDTLS_THREADING_C
#if defined(_WIN32)
#define MBEDTLS_THREADING_ALT
#else
#define MBEDTLS_THREADING_PTHREAD
#endif

/* mbedTLS detects the Armv8 crypto extensions at runtime on Linux only and assumes them elsewhere; Windows
 * on Arm does not guarantee them. */
#if defined(_WIN32) && defined(__aarch64__)
#undef MBEDTLS_AESCE_C
#endif

/* PSA otherwise copies every AEAD record and random output through a heap buffer. Safe here: no buffer is shared
 * with another party while a PSA call runs, and the pinned GCM and ChaCha20 drivers allow input == output. */
#define MBEDTLS_PSA_ASSUME_EXCLUSIVE_BUFFERS

/* GCM otherwise routes every AES counter block through the generic cipher layer. */
#undef PSA_WANT_ALG_CBC_NO_PADDING
#undef PSA_WANT_ALG_CBC_PKCS7
#undef PSA_WANT_ALG_CTR
#undef PSA_WANT_ALG_CFB
#undef PSA_WANT_ALG_OFB
#undef PSA_WANT_ALG_ECB_NO_PADDING
#undef PSA_WANT_ALG_STREAM_CIPHER
#undef PSA_WANT_ALG_CMAC
#undef PSA_WANT_ALG_CCM_STAR_NO_TAG
#undef PSA_WANT_ALG_PBKDF2_AES_CMAC_PRF_128
#undef MBEDTLS_NIST_KW_C

/* ECP self-test counters are shared by normal handshakes. */
#undef MBEDTLS_SELF_TEST

#undef MBEDTLS_DEBUG_C
#undef MBEDTLS_SSL_SRV_C
#undef MBEDTLS_SSL_CACHE_C
#undef MBEDTLS_SSL_TICKET_C
#undef MBEDTLS_SSL_COOKIE_C
#undef MBEDTLS_SSL_PROTO_DTLS
#undef MBEDTLS_SSL_DTLS_ANTI_REPLAY
#undef MBEDTLS_SSL_DTLS_CLIENT_PORT_REUSE
#undef MBEDTLS_SSL_DTLS_CONNECTION_ID
#undef MBEDTLS_SSL_DTLS_HELLO_VERIFY

#ifdef UGO_PLATFORM_CONFIG_FILE
#include UGO_PLATFORM_CONFIG_FILE
#endif
