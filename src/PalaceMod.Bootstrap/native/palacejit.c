// Writes to Mono's MAP_JIT code memory on Apple Silicon. While a thread has JIT write protection lifted, JIT
// memory is not executable for it, so the toggle, the copy and re-protecting must all happen here in native code.
#include <libkern/OSCacheControl.h>
#include <pthread.h>
#include <string.h>

void palace_jit_write(void *dst, const void *src, size_t size) {
    pthread_jit_write_protect_np(0);
    memcpy(dst, src, size);
    pthread_jit_write_protect_np(1);
    sys_icache_invalidate(dst, size);
}
