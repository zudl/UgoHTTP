#ifndef UGO_THREADING_ALT_H
#define UGO_THREADING_ALT_H

/* Stand-ins for SRWLOCK and CONDITION_VARIABLE (checked in platform/windows_tls.c): every curl and mbedTLS unit
 * includes this header, and windows.h would drag its macros into all of them. */
typedef struct { void *opaque; } mbedtls_platform_mutex_t;
typedef struct { void *opaque; } mbedtls_platform_condition_variable_t;

#endif
