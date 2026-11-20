#include "platform.h"

#import <UIKit/UIKit.h>

#include <stdatomic.h>

/* UIBackgroundTaskInvalid is 0, so task ids are never this value. */
#define TASK_CLAIMED UINTPTR_MAX

@interface UgoBackgroundTask : NSObject {
@public
    atomic_bool done;
    atomic_uintptr_t task;
}
@end

@implementation UgoBackgroundTask
@end

static void end_task(UgoBackgroundTask *bg)
{
    /* The expiration handler can run before beginBackgroundTask has returned the id: whichever of the
     * two sides comes second ends the task. */
    uintptr_t task = atomic_exchange(&bg->task, TASK_CLAIMED);
    if(task != UIBackgroundTaskInvalid && task != TASK_CLAIMED)
        [[UIApplication sharedApplication] endBackgroundTask:(UIBackgroundTaskIdentifier)task];
}

void *plat_bg_begin(plat_bg_expired expired, void *ctx)
{
    UIApplication *app = [UIApplication sharedApplication];
    if(!app)
        return NULL;
    UgoBackgroundTask *bg = [UgoBackgroundTask new];
    atomic_init(&bg->done, false);
    atomic_init(&bg->task, UIBackgroundTaskInvalid);
    void *handle = (__bridge void *)bg;
    UIBackgroundTaskIdentifier task = [app beginBackgroundTaskWithName:@"UgoHTTP" expirationHandler:^{
        if(atomic_exchange(&bg->done, true))
            return;
        expired(ctx, handle);
        end_task(bg);
    }];
    if(task == UIBackgroundTaskInvalid)
        return NULL;
    if(atomic_exchange(&bg->task, (uintptr_t)task) == TASK_CLAIMED)
        [app endBackgroundTask:task];
    return (__bridge_retained void *)bg;
}

int plat_bg_has_expired(void *handle)
{
    return handle && atomic_load(&((__bridge UgoBackgroundTask *)handle)->done);
}

void plat_bg_end(void *handle)
{
    if(!handle)
        return;
    UgoBackgroundTask *bg = (__bridge_transfer UgoBackgroundTask *)handle;
    if(!atomic_exchange(&bg->done, true))
        end_task(bg);
}
