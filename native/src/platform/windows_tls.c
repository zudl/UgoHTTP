#if defined(_WIN32)

#include "platform.h"

#include <mbedtls/threading.h>

_Static_assert(sizeof(mbedtls_platform_mutex_t) == sizeof(SRWLOCK), "threading_alt.h mutex size");
_Static_assert(sizeof(mbedtls_platform_condition_variable_t) == sizeof(CONDITION_VARIABLE),
               "threading_alt.h condition variable size");

static int mutex_init(mbedtls_platform_mutex_t *m)
{
    InitializeSRWLock((PSRWLOCK)m);
    return 0;
}

static void mutex_free(mbedtls_platform_mutex_t *m)
{
    (void)m;
}

static int mutex_lock(mbedtls_platform_mutex_t *m)
{
    AcquireSRWLockExclusive((PSRWLOCK)m);
    return 0;
}

static int mutex_unlock(mbedtls_platform_mutex_t *m)
{
    ReleaseSRWLockExclusive((PSRWLOCK)m);
    return 0;
}

static int cond_init(mbedtls_platform_condition_variable_t *c)
{
    InitializeConditionVariable((PCONDITION_VARIABLE)c);
    return 0;
}

static void cond_free(mbedtls_platform_condition_variable_t *c)
{
    (void)c;
}

static int cond_signal(mbedtls_platform_condition_variable_t *c)
{
    WakeConditionVariable((PCONDITION_VARIABLE)c);
    return 0;
}

static int cond_broadcast(mbedtls_platform_condition_variable_t *c)
{
    WakeAllConditionVariable((PCONDITION_VARIABLE)c);
    return 0;
}

static int cond_wait(mbedtls_platform_condition_variable_t *c, mbedtls_platform_mutex_t *m)
{
    return SleepConditionVariableSRW((PCONDITION_VARIABLE)c, (PSRWLOCK)m, INFINITE, 0)
        ? 0 : MBEDTLS_ERR_THREADING_USAGE_ERROR;
}

void plat_tls_init(void)
{
    mbedtls_threading_set_alt(mutex_init, mutex_free, mutex_lock, mutex_unlock, cond_init, cond_free,
                              cond_signal, cond_broadcast, cond_wait);
}

#endif
