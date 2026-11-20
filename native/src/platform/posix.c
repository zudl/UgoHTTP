#if !defined(_WIN32)

#if !defined(__APPLE__) && !defined(_GNU_SOURCE)
#  define _GNU_SOURCE
#endif

#include "platform.h"

#include <errno.h>
#include <stdbool.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <time.h>
#include <unistd.h>

#if defined(__APPLE__)
#  include <TargetConditionals.h>
#  include <mach/mach_time.h>
#endif

int plat_mutex_init(plat_mutex *m)
{
    return pthread_mutex_init(m, NULL) ? -1 : 0;
}

void plat_mutex_destroy(plat_mutex *m)
{
    pthread_mutex_destroy(m);
}

void plat_mutex_lock(plat_mutex *m)
{
    pthread_mutex_lock(m);
}

void plat_mutex_unlock(plat_mutex *m)
{
    pthread_mutex_unlock(m);
}

int plat_cond_init(plat_cond *c)
{
#if defined(__APPLE__)
    return pthread_cond_init(c, NULL) ? -1 : 0;
#else
    pthread_condattr_t attr;
    if(pthread_condattr_init(&attr))
        return -1;
    int failed = pthread_condattr_setclock(&attr, CLOCK_MONOTONIC) || pthread_cond_init(c, &attr);
    pthread_condattr_destroy(&attr);
    return failed ? -1 : 0;
#endif
}

void plat_cond_destroy(plat_cond *c)
{
    pthread_cond_destroy(c);
}

void plat_cond_signal(plat_cond *c)
{
    pthread_cond_signal(c);
}

void plat_cond_broadcast(plat_cond *c)
{
    pthread_cond_broadcast(c);
}

void plat_cond_wait(plat_cond *c, plat_mutex *m)
{
    pthread_cond_wait(c, m);
}

void plat_cond_wait_ms(plat_cond *c, plat_mutex *m, uint32_t ms)
{
    struct timespec ts;
#if defined(__APPLE__)
    ts.tv_sec = ms / 1000;
    ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    pthread_cond_timedwait_relative_np(c, m, &ts);
#else
    clock_gettime(CLOCK_MONOTONIC, &ts);
    ts.tv_sec += ms / 1000;
    ts.tv_nsec += (long)(ms % 1000) * 1000000L;
    if(ts.tv_nsec >= 1000000000L) {
        ts.tv_sec += 1;
        ts.tv_nsec -= 1000000000L;
    }
    pthread_cond_timedwait(c, m, &ts);
#endif
}

static void *thread_entry(void *arg)
{
    owner_main(arg);
    return NULL;
}

int plat_thread_start(plat_thread *t, void *arg)
{
    return pthread_create(t, NULL, thread_entry, arg) ? -1 : 0;
}

void plat_thread_join(plat_thread t)
{
    pthread_join(t, NULL);
}

static pthread_once_t once = PTHREAD_ONCE_INIT;

int plat_once(void (*fn)(void))
{
    return pthread_once(&once, fn) ? -1 : 0;
}

void plat_tls_init(void)
{
}

int64_t plat_now_us(void)
{
#if defined(__APPLE__)
    mach_timebase_info_data_t base;
    mach_timebase_info(&base);
    return (int64_t)(mach_continuous_time() * base.numer / base.denom / 1000);
#else
    struct timespec ts;
#  if defined(__linux__)
    clock_gettime(CLOCK_BOOTTIME, &ts);
#  else
    clock_gettime(CLOCK_MONOTONIC, &ts);
#  endif
    return (int64_t)ts.tv_sec * 1000000 + ts.tv_nsec / 1000;
#endif
}

/* O_NONBLOCK keeps a FIFO from blocking the owner in open(); only regular files are accepted, and on those the
 * flag has no effect. */
static plat_file open_regular(const char *path, int flags)
{
    int fd;
    do
        fd = open(path, flags | O_NONBLOCK | O_CLOEXEC, 0666);
    while(fd < 0 && errno == EINTR);
    if(fd < 0)
        return PLAT_NO_FILE;
    struct stat st;
    if(fstat(fd, &st) || !S_ISREG(st.st_mode)) {
        close(fd);
        return PLAT_NO_FILE;
    }
    return (plat_file)fd;
}

plat_file plat_file_create(const char *path)
{
    return open_regular(path, O_WRONLY | O_CREAT | O_TRUNC);
}

plat_file plat_file_open_read(const char *path)
{
    return open_regular(path, O_RDONLY);
}

plat_file plat_file_open_write(const char *path)
{
    return open_regular(path, O_WRONLY);
}

int plat_file_write(plat_file f, const void *data, size_t len)
{
    const char *p = data;
    while(len) {
        ssize_t n = write((int)f, p, len);
        if(n < 0 && errno == EINTR)
            continue;
        if(n <= 0)
            return -1;
        p += n;
        len -= (size_t)n;
    }
    return 0;
}

int64_t plat_file_read(plat_file f, void *data, size_t len)
{
    ssize_t n;
    do
        n = read((int)f, data, len);
    while(n < 0 && errno == EINTR);
    return n;
}

int plat_file_seek(plat_file f, uint64_t offset)
{
    return lseek((int)f, (off_t)offset, SEEK_SET) < 0 ? -1 : 0;
}

int plat_file_truncate(plat_file f, uint64_t size)
{
    int rc;
    do
        rc = ftruncate((int)f, (off_t)size);
    while(rc && errno == EINTR);
    return rc ? -1 : plat_file_seek(f, size);
}

int64_t plat_file_size(plat_file f)
{
    struct stat st;
    if(fstat((int)f, &st) || !S_ISREG(st.st_mode))
        return -1;
    return st.st_size;
}

int plat_file_close(plat_file f)
{
    /* POSIX leaves the descriptor state unspecified after EINTR: never retry close. */
    return close((int)f) && errno != EINTR ? -1 : 0;
}

int plat_file_remove(const char *path)
{
    return unlink(path) && errno != ENOENT ? -1 : 0;
}

#if !(defined(__APPLE__) && TARGET_OS_IOS)
#if defined(UGO_TEST_COUNTERS)
/* Set by tests: begin behaves like iOS running the expiration handler before beginBackgroundTask returns. */
bool ugo_test_bg_expire_early;
static int test_bg;
#endif

void *plat_bg_begin(plat_bg_expired expired, void *ctx)
{
#if defined(UGO_TEST_COUNTERS)
    if(__atomic_load_n(&ugo_test_bg_expire_early, __ATOMIC_RELAXED)) {
        expired(ctx, &test_bg);
        return &test_bg;
    }
#endif
    (void)expired;
    (void)ctx;
    return NULL;
}

int plat_bg_has_expired(void *bg)
{
#if defined(UGO_TEST_COUNTERS)
    return bg == &test_bg;
#else
    (void)bg;
    return 0;
#endif
}

void plat_bg_end(void *bg)
{
    (void)bg;
}
#endif

#endif
