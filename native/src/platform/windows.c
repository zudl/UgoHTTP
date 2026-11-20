#if defined(_WIN32)

#include "platform.h"

#include <stdlib.h>

int plat_mutex_init(plat_mutex *m)
{
    InitializeSRWLock(m);
    return 0;
}

void plat_mutex_destroy(plat_mutex *m)
{
    (void)m;
}

void plat_mutex_lock(plat_mutex *m)
{
    AcquireSRWLockExclusive(m);
}

void plat_mutex_unlock(plat_mutex *m)
{
    ReleaseSRWLockExclusive(m);
}

int plat_cond_init(plat_cond *c)
{
    InitializeConditionVariable(c);
    return 0;
}

void plat_cond_destroy(plat_cond *c)
{
    (void)c;
}

void plat_cond_signal(plat_cond *c)
{
    WakeConditionVariable(c);
}

void plat_cond_broadcast(plat_cond *c)
{
    WakeAllConditionVariable(c);
}

void plat_cond_wait(plat_cond *c, plat_mutex *m)
{
    SleepConditionVariableSRW(c, m, INFINITE, 0);
}

void plat_cond_wait_ms(plat_cond *c, plat_mutex *m, uint32_t ms)
{
    SleepConditionVariableSRW(c, m, ms == INFINITE ? INFINITE - 1 : ms, 0);
}

static DWORD WINAPI thread_entry(LPVOID arg)
{
    owner_main(arg);
    return 0;
}

int plat_thread_start(plat_thread *t, void *arg)
{
    *t = CreateThread(NULL, 0, thread_entry, arg, 0, NULL);
    return *t ? 0 : -1;
}

void plat_thread_join(plat_thread t)
{
    WaitForSingleObject(t, INFINITE);
    CloseHandle(t);
}

static INIT_ONCE once = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK once_entry(PINIT_ONCE o, PVOID fn, PVOID *ctx)
{
    (void)o;
    (void)ctx;
    ((void (*)(void))fn)();
    return TRUE;
}

int plat_once(void (*fn)(void))
{
    return InitOnceExecuteOnce(&once, once_entry, (PVOID)fn, NULL) ? 0 : -1;
}

int64_t plat_now_us(void)
{
    ULONGLONG t;
    QueryInterruptTime(&t);
    return (int64_t)(t / 10);
}

static wchar_t *wide(const char *path)
{
    int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
    if(n <= 0)
        return NULL;
    wchar_t *w = malloc((size_t)n * sizeof(wchar_t));
    if(w && MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, w, n) != n) {
        free(w);
        w = NULL;
    }
    return w;
}

static plat_file open_path(const char *path, DWORD access, DWORD disposition)
{
    wchar_t *w = wide(path);
    if(!w)
        return PLAT_NO_FILE;
    HANDLE h = CreateFileW(w, access, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, disposition,
                           FILE_ATTRIBUTE_NORMAL, NULL);
    free(w);
    if(h == INVALID_HANDLE_VALUE)
        return PLAT_NO_FILE;
    if(GetFileType(h) != FILE_TYPE_DISK) {
        CloseHandle(h);
        return PLAT_NO_FILE;
    }
    return (plat_file)h;
}

plat_file plat_file_create(const char *path)
{
    return open_path(path, GENERIC_WRITE, CREATE_ALWAYS);
}

plat_file plat_file_open_read(const char *path)
{
    return open_path(path, GENERIC_READ, OPEN_EXISTING);
}

plat_file plat_file_open_write(const char *path)
{
    return open_path(path, GENERIC_WRITE, OPEN_EXISTING);
}

int plat_file_write(plat_file f, const void *data, size_t len)
{
    const char *p = data;
    while(len) {
        DWORD chunk = len > 0x40000000u ? 0x40000000u : (DWORD)len;
        DWORD written = 0;
        if(!WriteFile((HANDLE)f, p, chunk, &written, NULL) || !written)
            return -1;
        p += written;
        len -= written;
    }
    return 0;
}

int64_t plat_file_read(plat_file f, void *data, size_t len)
{
    DWORD n = 0;
    if(!ReadFile((HANDLE)f, data, len > 0x40000000u ? 0x40000000u : (DWORD)len, &n, NULL))
        return -1;
    return n;
}

int plat_file_seek(plat_file f, uint64_t offset)
{
    LARGE_INTEGER to;
    to.QuadPart = (LONGLONG)offset;
    return SetFilePointerEx((HANDLE)f, to, NULL, FILE_BEGIN) ? 0 : -1;
}

int plat_file_truncate(plat_file f, uint64_t size)
{
    return plat_file_seek(f, size) || !SetEndOfFile((HANDLE)f) ? -1 : 0;
}

int64_t plat_file_size(plat_file f)
{
    LARGE_INTEGER size;
    return GetFileSizeEx((HANDLE)f, &size) ? size.QuadPart : -1;
}

int plat_file_close(plat_file f)
{
    return CloseHandle((HANDLE)f) ? 0 : -1;
}

int plat_file_remove(const char *path)
{
    wchar_t *w = wide(path);
    if(!w)
        return -1;
    BOOL ok = DeleteFileW(w) || GetLastError() == ERROR_FILE_NOT_FOUND;
    free(w);
    return ok ? 0 : -1;
}

void *plat_bg_begin(plat_bg_expired expired, void *ctx)
{
    (void)expired;
    (void)ctx;
    return NULL;
}

int plat_bg_has_expired(void *bg)
{
    (void)bg;
    return 0;
}

void plat_bg_end(void *bg)
{
    (void)bg;
}

#endif
