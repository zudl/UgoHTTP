#ifndef UGO_PLATFORM_H
#define UGO_PLATFORM_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  ifndef WIN32_LEAN_AND_MEAN
#    define WIN32_LEAN_AND_MEAN
#  endif
#  include <windows.h>
typedef SRWLOCK plat_mutex;
typedef CONDITION_VARIABLE plat_cond;
typedef HANDLE plat_thread;
#  define PLAT_MUTEX_INIT SRWLOCK_INIT
#else
#  include <pthread.h>
typedef pthread_mutex_t plat_mutex;
typedef pthread_cond_t plat_cond;
typedef pthread_t plat_thread;
#  define PLAT_MUTEX_INIT PTHREAD_MUTEX_INITIALIZER
#endif

typedef intptr_t plat_file;
#define PLAT_NO_FILE ((plat_file)-1)

int plat_mutex_init(plat_mutex *m);
void plat_mutex_destroy(plat_mutex *m);
void plat_mutex_lock(plat_mutex *m);
void plat_mutex_unlock(plat_mutex *m);

int plat_cond_init(plat_cond *c);
void plat_cond_destroy(plat_cond *c);
void plat_cond_signal(plat_cond *c);
void plat_cond_broadcast(plat_cond *c);
void plat_cond_wait(plat_cond *c, plat_mutex *m);
void plat_cond_wait_ms(plat_cond *c, plat_mutex *m, uint32_t ms);

void owner_main(void *arg);
int plat_thread_start(plat_thread *t, void *arg);
void plat_thread_join(plat_thread t);

int plat_once(void (*fn)(void));
/* mbedTLS threading where it has no built-in backend (Windows); must run before any mbedTLS call. */
void plat_tls_init(void);
int64_t plat_now_us(void);

/* Paths are NUL-terminated UTF-8. */
plat_file plat_file_create(const char *path);
plat_file plat_file_open_read(const char *path);
/* An existing regular file for writing at offset 0, neither created nor truncated. */
plat_file plat_file_open_write(const char *path);
int plat_file_write(plat_file f, const void *data, size_t len);
int64_t plat_file_read(plat_file f, void *data, size_t len);
int plat_file_seek(plat_file f, uint64_t offset);
/* Sets the length to `size` and the position to its end. */
int plat_file_truncate(plat_file f, uint64_t size);
int64_t plat_file_size(plat_file f);
int plat_file_close(plat_file f);
int plat_file_remove(const char *path);

/* iOS background task (beginBackgroundTask); NULL and no-ops elsewhere. begin may run on any thread.
 * The task is ended exactly once, by plat_bg_end or by the expiration, whichever comes first. On expiration
 * `expired(ctx, bg)` runs on the main thread before the task ends, unless plat_bg_end already ran; it can run
 * before plat_bg_begin returns, so a caller that publishes bg afterwards checks plat_bg_has_expired. */
typedef void (*plat_bg_expired)(void *ctx, void *bg);
void *plat_bg_begin(plat_bg_expired expired, void *ctx);
int plat_bg_has_expired(void *bg);
void plat_bg_end(void *bg);

#endif
