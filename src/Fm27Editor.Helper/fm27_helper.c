/*
 * fm27_helper.c - on-demand privileged Mach-VM helper for FM27 Editor.
 *
 * launchd starts this process only when a client connects to the socket named
 * "Fm27Socket" (SockPathName=/var/run/fm27editor.sock). It performs the Mach VM
 * calls the unprivileged editor is not allowed to make (task_for_pid,
 * mach_vm_read/write/allocate/map/protect), then exits after a short idle
 * period so launchd can start it again on the next connection.
 *
 * Protocol (all integers little-endian):
 *   request  = uint32 payload_length, then payload
 *   payload  = uint8 opcode, then opcode arguments
 *   response = uint32 payload_length, then payload
 *   payload  = int32 status (0 ok, 1 error), then data
 *   on error: int32 code, uint32 message_length, message bytes
 *
 * Opcodes:
 *   1 Open(pid:i32, writable:u8)            -> handle:i32
 *   2 Read(handle:i32, addr:u64, len:u32)   -> data bytes
 *   3 Write(handle:i32, addr:u64, len:u32, data)
 *   4 Allocate(handle:i32, bytes:u32)       -> addr:u64
 *   5 AllocateNear(handle:i32, near:u64, bytes:u32) -> addr:u64
 *   6 Protect(handle:i32, addr:u64, bytes:u32, vm_prot:u32, set_max:u8) -> old_page:u32
 *   7 Free(handle:i32, addr:u64, bytes:u64)
 *   8 Enumerate(handle:i32, start:u64, end:u64) -> count:u32, records
 *   9 Close(handle:i32)
 *  10 DyldInfo(handle:i32)                  -> info_addr:u64, info_size:u64
 *  11 Pause(handle:i32)                     -> token:u32
 *  12 Resume(handle:i32)
 *  13 ReadMany(handle:i32, count:u32, [addr:u64, len:u32]) -> count:u32, [status:i32, len:u32, data]
 */

#include <errno.h>
#include <launch.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <poll.h>
#include <signal.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <time.h>
#include <unistd.h>

#define MAX_TASKS 32
#define MAX_MESSAGE (256u * 1024u * 1024u)
#define IDLE_SECONDS 5
#define SUBMAP_MAX_DEPTH 16
#define ALLOC_GRANULARITY 0x4000ULL
#define BRANCH_REACH 0x08000000ULL
#define MAX_SCATTER_READS 1024u
#define MAX_SCATTER_BYTES 32u

/* VM_PROT_READ/WRITE/EXECUTE come from <mach/vm_prot.h>. */
#define VM_PROT_RW (VM_PROT_READ | VM_PROT_WRITE)
#define VM_PROT_RWX (VM_PROT_READ | VM_PROT_WRITE | VM_PROT_EXECUTE)

/* Windows PAGE_* values the editor's code-patch presets use. */
#define PAGE_READONLY 0x02
#define PAGE_READWRITE 0x04
#define PAGE_EXECUTE_READ 0x20

enum
{
    OP_OPEN = 1,
    OP_READ = 2,
    OP_WRITE = 3,
    OP_ALLOCATE = 4,
    OP_ALLOCATE_NEAR = 5,
    OP_PROTECT = 6,
    OP_FREE = 7,
    OP_ENUMERATE = 8,
    OP_CLOSE = 9,
    OP_DYLD_INFO = 10,
    OP_PAUSE = 11,
    OP_RESUME = 12,
    OP_READ_MANY = 13
};

typedef struct
{
    uint8_t *data;
    size_t len;
    size_t cap;
} buffer;

typedef struct
{
    int used;
    int owner;
    mach_port_t task;
    pid_t pid;
    int paused;
    thread_act_array_t threads;
    mach_msg_type_number_t thread_count;
} task_slot;

typedef struct
{
    uint64_t start;
    uint64_t end;
} interval;

typedef struct
{
    const uint8_t *p;
    const uint8_t *end;
} reader;

static task_slot g_tasks[MAX_TASKS];

/* ------------------------------------------------------------------ buffer */

static int buf_reserve(buffer *b, size_t extra)
{
    if (extra > MAX_MESSAGE)
        return -1;
    if (b->len + extra <= b->cap)
        return 0;
    size_t cap = b->cap ? b->cap : 256;
    while (cap < b->len + extra)
    {
        if (cap > MAX_MESSAGE)
            return -1;
        cap *= 2;
    }
    uint8_t *p = realloc(b->data, cap);
    if (p == NULL)
        return -1;
    b->data = p;
    b->cap = cap;
    return 0;
}

static int buf_u8(buffer *b, uint8_t v)
{
    if (buf_reserve(b, 1) != 0)
        return -1;
    b->data[b->len++] = v;
    return 0;
}

static int buf_u32(buffer *b, uint32_t v)
{
    if (buf_reserve(b, 4) != 0)
        return -1;
    memcpy(b->data + b->len, &v, 4);
    b->len += 4;
    return 0;
}

static int buf_u64(buffer *b, uint64_t v)
{
    if (buf_reserve(b, 8) != 0)
        return -1;
    memcpy(b->data + b->len, &v, 8);
    b->len += 8;
    return 0;
}

static int buf_bytes(buffer *b, const void *data, size_t len)
{
    if (len > MAX_MESSAGE || buf_reserve(b, len) != 0)
        return -1;
    if (len > 0)
        memcpy(b->data + b->len, data, len);
    b->len += len;
    return 0;
}

/* ------------------------------------------------------------------ reader */

static int rd_u8(reader *r, uint8_t *v)
{
    if (r->p + 1 > r->end)
        return 0;
    *v = *r->p++;
    return 1;
}

static int rd_u32(reader *r, uint32_t *v)
{
    if (r->p + 4 > r->end)
        return 0;
    memcpy(v, r->p, 4);
    r->p += 4;
    return 1;
}

static int rd_u64(reader *r, uint64_t *v)
{
    if (r->p + 8 > r->end)
        return 0;
    memcpy(v, r->p, 8);
    r->p += 8;
    return 1;
}

static int rd_i32(reader *r, int32_t *v)
{
    uint32_t u;
    if (!rd_u32(r, &u))
        return 0;
    *v = (int32_t)u;
    return 1;
}

/* -------------------------------------------------------------- socket I/O */

static int write_exact(int fd, const void *data, size_t len)
{
    const uint8_t *p = data;
    while (len > 0)
    {
        ssize_t n = write(fd, p, len);
        if (n < 0)
        {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (n == 0)
            return -1;
        p += (size_t)n;
        len -= (size_t)n;
    }
    return 0;
}

static int read_exact(int fd, void *data, size_t len)
{
    uint8_t *p = data;
    while (len > 0)
    {
        ssize_t n = read(fd, p, len);
        if (n < 0)
        {
            if (errno == EINTR)
                continue;
            return -1;
        }
        if (n == 0)
            return -1;
        p += (size_t)n;
        len -= (size_t)n;
    }
    return 0;
}

static int send_frame(int fd, const void *data, uint32_t len)
{
    uint32_t n = len;
    if (write_exact(fd, &n, 4) != 0)
        return -1;
    if (len > 0 && write_exact(fd, data, len) != 0)
        return -1;
    return 0;
}

static void send_error(int fd, int32_t code, const char *message)
{
    buffer b = {0};
    uint32_t len = message ? (uint32_t)strlen(message) : 0;
    if (buf_u32(&b, 1) != 0 || buf_u32(&b, (uint32_t)code) != 0 ||
        buf_u32(&b, len) != 0 || buf_bytes(&b, message, len) != 0)
    {
        free(b.data);
        return;
    }
    send_frame(fd, b.data, (uint32_t)b.len);
    free(b.data);
}

static void send_errorf(int fd, int32_t code, const char *format, ...)
{
    char message[512];
    va_list args;
    va_start(args, format);
    vsnprintf(message, sizeof(message), format, args);
    va_end(args);
    send_error(fd, code, message);
}

/* ------------------------------------------------------------- task slots */

static task_slot *slot_for(int32_t handle)
{
    if (handle < 1 || handle > MAX_TASKS)
        return NULL;
    task_slot *s = &g_tasks[handle - 1];
    return s->used ? s : NULL;
}

static void resume_task(task_slot *s)
{
    if (!s->paused)
        return;
    for (mach_msg_type_number_t i = 0; i < s->thread_count; i++)
    {
        thread_resume(s->threads[i]);
        mach_port_deallocate(mach_task_self(), s->threads[i]);
    }
    if (s->threads != NULL)
        mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)s->threads,
                           (mach_vm_size_t)s->thread_count * sizeof(thread_act_t));
    s->threads = NULL;
    s->thread_count = 0;
    s->paused = 0;
}

static void close_slot(task_slot *s)
{
    if (!s->used)
        return;
    resume_task(s);
    if (s->task != MACH_PORT_NULL)
        mach_port_deallocate(mach_task_self(), s->task);
    *s = (task_slot){0};
}

static void close_client_tasks(int client_id)
{
    for (int i = 0; i < MAX_TASKS; i++)
        if (g_tasks[i].used && g_tasks[i].owner == client_id)
            close_slot(&g_tasks[i]);
}

/* ------------------------------------------------------------ Mach regions */

static int query_region(task_t task, mach_vm_address_t *address, mach_vm_size_t *size,
                        int *protection, int *is_submap, natural_t *depth)
{
    vm_region_submap_info_data_64_t info;
    mach_msg_type_number_t count = VM_REGION_SUBMAP_INFO_COUNT_64;
    memset(&info, 0, sizeof(info));
    kern_return_t kr = mach_vm_region_recurse(task, address, size, depth,
                                              (vm_region_recurse_info_t)&info, &count);
    if (kr != KERN_SUCCESS || *size == 0)
        return 0;
    *protection = (int)info.protection;
    *is_submap = info.is_submap ? 1 : 0;
    return 1;
}

static int current_protection(task_t task, uint64_t address, int *protection)
{
    mach_vm_address_t addr = 0;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &addr, &size, &prot, &sub, &depth))
            return 0;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                return 0;
            depth++;
            continue;
        }
        if (address >= (uint64_t)addr && address < (uint64_t)addr + (uint64_t)size)
        {
            *protection = prot;
            return 1;
        }
        uint64_t next = (uint64_t)addr + (uint64_t)size;
        if (next <= (uint64_t)addr)
            return 0;
        addr = (mach_vm_address_t)next;
        depth = 1;
    }
}

static uint32_t from_vm_protection(int protection)
{
    if ((protection & VM_PROT_EXECUTE) != 0)
        return PAGE_EXECUTE_READ;
    if ((protection & VM_PROT_WRITE) != 0)
        return PAGE_READWRITE;
    if (protection == 0)
        return PAGE_EXECUTE_READ;
    return PAGE_READONLY;
}

static int collect_writable(task_t task, uint64_t image_start, uint64_t image_end, buffer *records)
{
    mach_vm_address_t address = 0;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &address, &size, &prot, &sub, &depth))
            break;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                break;
            depth++;
            continue;
        }
        if ((prot & VM_PROT_WRITE) != 0)
        {
            uint64_t start = (uint64_t)address;
            uint8_t module = (start >= image_start && start < image_end) ? 1 : 0;
            if (buf_u64(records, start) != 0 || buf_u64(records, (uint64_t)size) != 0 ||
                buf_u8(records, module) != 0)
                return -1;
        }
        uint64_t next = (uint64_t)address + (uint64_t)size;
        if (next <= (uint64_t)address)
            break;
        address = (mach_vm_address_t)next;
        depth = 1;
    }
    return 0;
}

static int collect_mapped(task_t task, uint64_t low, uint64_t high, interval **out, size_t *out_count)
{
    size_t cap = 64;
    size_t n = 0;
    interval *ranges = malloc(cap * sizeof(interval));
    if (ranges == NULL)
        return -1;

    mach_vm_address_t address = (mach_vm_address_t)low;
    natural_t depth = 1;
    for (;;)
    {
        mach_vm_size_t size = 0;
        int prot = 0;
        int sub = 0;
        if (!query_region(task, &address, &size, &prot, &sub, &depth))
            break;
        if (sub)
        {
            if (depth >= SUBMAP_MAX_DEPTH)
                break;
            depth++;
            continue;
        }
        uint64_t start = (uint64_t)address;
        uint64_t end = start + (uint64_t)size;
        if (end <= start)
            break;
        if (start < high && end > low)
        {
            if (start < low)
                start = low;
            if (end > high)
                end = high;
            if (n == cap)
            {
                size_t new_cap = cap * 2;
                interval *grown = realloc(ranges, new_cap * sizeof(interval));
                if (grown == NULL)
                {
                    free(ranges);
                    return -1;
                }
                ranges = grown;
                cap = new_cap;
            }
            ranges[n].start = start;
            ranges[n].end = end;
            n++;
        }
        address = (mach_vm_address_t)end;
        depth = 1;
        if (end >= high)
            break;
    }
    *out = ranges;
    *out_count = n;
    return 0;
}

static int try_map_at(task_t task, uint64_t at, uint64_t size, uint64_t *result)
{
    mach_vm_address_t address = (mach_vm_address_t)at;
    kern_return_t kr = mach_vm_map(task, &address, (mach_vm_size_t)size, 0, VM_FLAGS_FIXED,
                                   MEMORY_OBJECT_NULL, 0, FALSE, VM_PROT_RW,
                                   VM_PROT_RWX, VM_INHERIT_DEFAULT);
    if (kr != KERN_SUCCESS)
        return 0;
    *result = (uint64_t)address;
    return 1;
}

static int allocate_near(task_t task, uint64_t near, uint64_t bytes, uint64_t *result)
{
    int ok = 0;
    uint64_t size = (bytes + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    uint64_t low = near > BRANCH_REACH ? near - BRANCH_REACH : ALLOC_GRANULARITY;
    low = (low + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    uint64_t high = near + BRANCH_REACH;

    interval *ranges = NULL;
    size_t range_count = 0;
    if (collect_mapped(task, low, high, &ranges, &range_count) != 0)
        return 0;

    uint64_t *holes = NULL;
    size_t hole_count = 0;
    size_t hole_cap = 0;

    uint64_t cursor = low;
    for (size_t i = 0; i < range_count; i++)
    {
        uint64_t candidate = (cursor + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
        if (ranges[i].start > candidate && ranges[i].start - candidate >= size)
        {
            if (hole_count == hole_cap)
            {
                size_t new_cap = hole_cap ? hole_cap * 2 : 16;
                uint64_t *grown = realloc(holes, new_cap * sizeof(uint64_t));
                if (grown == NULL)
                    goto done;
                holes = grown;
                hole_cap = new_cap;
            }
            holes[hole_count++] = candidate;
        }
        if (ranges[i].end > cursor)
            cursor = ranges[i].end;
    }
    uint64_t tail = (cursor + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    if (high > tail && high - tail >= size)
    {
        if (hole_count == hole_cap)
        {
            size_t new_cap = hole_cap ? hole_cap * 2 : 16;
            uint64_t *grown = realloc(holes, new_cap * sizeof(uint64_t));
            if (grown == NULL)
                goto done;
            holes = grown;
            hole_cap = new_cap;
        }
        holes[hole_count++] = tail;
    }
    free(ranges);
    ranges = NULL;

    /* Nearest hole to the branch site first. */
    for (size_t i = 1; i < hole_count; i++)
    {
        uint64_t value = holes[i];
        size_t j = i;
        while (j > 0)
        {
            uint64_t prev = holes[j - 1];
            uint64_t dv = value > near ? value - near : near - value;
            uint64_t dp = prev > near ? prev - near : near - prev;
            if (dp <= dv)
                break;
            holes[j] = prev;
            j--;
        }
        holes[j] = value;
    }

    for (size_t i = 0; i < hole_count; i++)
        if (try_map_at(task, holes[i], size, result))
        {
            ok = 1;
            goto done;
        }

    /* Nothing free within reach of a branch: take any hole and check the distance. */
    {
        mach_vm_address_t anywhere = 0;
        kern_return_t kr = mach_vm_map(task, &anywhere, (mach_vm_size_t)size, 0, VM_FLAGS_ANYWHERE,
                                       MEMORY_OBJECT_NULL, 0, FALSE, VM_PROT_RW,
                                       VM_PROT_RWX, VM_INHERIT_DEFAULT);
        if (kr == KERN_SUCCESS)
        {
            uint64_t at = (uint64_t)anywhere;
            int in_reach = at >= near
                ? (at + size - near) <= BRANCH_REACH
                : (near - at) <= BRANCH_REACH;
            if (in_reach)
            {
                *result = at;
                ok = 1;
                goto done;
            }
            mach_vm_deallocate(task, anywhere, (mach_vm_size_t)size);
        }
    }

done:
    free(holes);
    free(ranges);
    return ok;
}

/* -------------------------------------------------------------- operations */

static void op_open(int fd, int client_id, reader *r)
{
    int32_t pid;
    uint8_t writable;
    if (!rd_i32(r, &pid) || !rd_u8(r, &writable))
    {
        send_error(fd, 1, "malformed Open request");
        return;
    }
    (void)writable;

    mach_port_t task = MACH_PORT_NULL;
    kern_return_t kr = task_for_pid(mach_task_self(), pid, &task);
    if (kr != KERN_SUCCESS || task == MACH_PORT_NULL)
    {
        send_errorf(fd, 2, "task_for_pid(%d) failed with kern_return_t %d", (int)pid, (int)kr);
        return;
    }

    int slot = -1;
    for (int i = 0; i < MAX_TASKS; i++)
        if (!g_tasks[i].used)
        {
            slot = i;
            break;
        }
    if (slot < 0)
    {
        mach_port_deallocate(mach_task_self(), task);
        send_error(fd, 3, "too many open tasks");
        return;
    }

    g_tasks[slot] = (task_slot){0};
    g_tasks[slot].used = 1;
    g_tasks[slot].owner = client_id;
    g_tasks[slot].task = task;
    g_tasks[slot].pid = pid;

    buffer out = {0};
    if (buf_u32(&out, 0) != 0 || buf_u32(&out, (uint32_t)(slot + 1)) != 0)
    {
        free(out.data);
        send_error(fd, 4, "out of memory");
        return;
    }
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_read(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t length;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &length))
    {
        send_error(fd, 1, "malformed Read request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    buffer out = {0};
    if (length == 0)
    {
        buf_u32(&out, 0);
        send_frame(fd, out.data, (uint32_t)out.len);
        free(out.data);
        return;
    }
    if (length > MAX_MESSAGE)
    {
        send_error(fd, 6, "read is too large");
        return;
    }

    uint8_t *data = malloc(length);
    if (data == NULL)
    {
        send_error(fd, 4, "out of memory");
        return;
    }
    mach_vm_size_t got = 0;
    kern_return_t kr = mach_vm_read_overwrite(slot->task, (mach_vm_address_t)address,
                                              (mach_vm_size_t)length, (mach_vm_address_t)data, &got);
    if (kr != KERN_SUCCESS || got != (mach_vm_size_t)length)
    {
        free(data);
        send_errorf(fd, 7, "mach_vm_read_overwrite(0x%llx, %u) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)length, (int)kr);
        return;
    }
    if (buf_u32(&out, 0) != 0 || buf_bytes(&out, data, length) != 0)
    {
        free(data);
        free(out.data);
        send_error(fd, 4, "out of memory");
        return;
    }
    free(data);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

/* Read-only batching of small fields; the complete request is checked before the first Mach read. */
static void op_read_many(int fd, reader *r)
{
    int32_t handle;
    uint32_t count;
    uint64_t addresses[MAX_SCATTER_READS];
    uint32_t lengths[MAX_SCATTER_READS];
    if (!rd_i32(r, &handle) || !rd_u32(r, &count) || count == 0 || count > MAX_SCATTER_READS ||
        (size_t)(r->end - r->p) != (size_t)count * 12)
    {
        send_error(fd, 1, "malformed ReadMany request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    for (uint32_t i = 0; i < count; i++)
    {
        if (!rd_u64(r, &addresses[i]) || !rd_u32(r, &lengths[i]) ||
            lengths[i] == 0 || lengths[i] > MAX_SCATTER_BYTES)
        {
            send_error(fd, 1, "invalid ReadMany field length");
            return;
        }
    }

    buffer out = {0};
    if (buf_reserve(&out, 8 + (size_t)count * (8 + MAX_SCATTER_BYTES)) != 0)
    {
        send_error(fd, 4, "out of memory");
        return;
    }
    buf_u32(&out, 0);
    buf_u32(&out, count);
    for (uint32_t i = 0; i < count; i++)
    {
        uint8_t data[MAX_SCATTER_BYTES];
        mach_vm_size_t got = 0;
        kern_return_t kr = mach_vm_read_overwrite(slot->task, (mach_vm_address_t)addresses[i],
            (mach_vm_size_t)lengths[i], (mach_vm_address_t)data, &got);
        int ok = kr == KERN_SUCCESS && got == (mach_vm_size_t)lengths[i];
        buf_u32(&out, ok ? 0 : 1);
        buf_u32(&out, ok ? lengths[i] : 0);
        if (ok) buf_bytes(&out, data, lengths[i]);
    }
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_write(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t length;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &length))
    {
        send_error(fd, 1, "malformed Write request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    if ((uint64_t)(r->end - r->p) < (uint64_t)length)
    {
        send_error(fd, 1, "Write data is shorter than its length");
        return;
    }
    if (length == 0)
    {
        buffer out = {0};
        buf_u32(&out, 0);
        send_frame(fd, out.data, (uint32_t)out.len);
        free(out.data);
        return;
    }

    kern_return_t kr = mach_vm_write(slot->task, (mach_vm_address_t)address,
                                     (vm_offset_t)(uintptr_t)r->p, (mach_msg_type_number_t)length);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 7, "mach_vm_write(0x%llx, %u) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)length, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_allocate(int fd, reader *r)
{
    int32_t handle;
    uint32_t bytes;
    if (!rd_i32(r, &handle) || !rd_u32(r, &bytes))
    {
        send_error(fd, 1, "malformed Allocate request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    mach_vm_address_t address = 0;
    kern_return_t kr = mach_vm_allocate(slot->task, &address, (mach_vm_size_t)bytes, VM_FLAGS_ANYWHERE);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 7, "mach_vm_allocate(%u) failed with kern_return_t %d", (unsigned)bytes, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, (uint64_t)address);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_allocate_near(int fd, reader *r)
{
    int32_t handle;
    uint64_t near;
    uint32_t bytes;
    if (!rd_i32(r, &handle) || !rd_u64(r, &near) || !rd_u32(r, &bytes))
    {
        send_error(fd, 1, "malformed AllocateNear request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    uint64_t address = 0;
    if (!allocate_near(slot->task, near, bytes, &address))
    {
        send_errorf(fd, 8, "no free memory within %llu MB of 0x%llx",
                    (unsigned long long)(BRANCH_REACH >> 20), (unsigned long long)near);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, address);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_protect(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint32_t bytes;
    uint32_t protection;
    uint8_t copy;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u32(r, &bytes) ||
        !rd_u32(r, &protection) || !rd_u8(r, &copy))
    {
        send_error(fd, 1, "malformed Protect request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    int previous = 0;
    uint32_t previous_page = PAGE_EXECUTE_READ;
    if (current_protection(slot->task, address, &previous))
        previous_page = from_vm_protection(previous);

    kern_return_t kr = mach_vm_protect(slot->task, (mach_vm_address_t)address, (mach_vm_size_t)bytes,
                                       copy ? TRUE : FALSE, (vm_prot_t)protection);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 9, "mach_vm_protect(0x%llx, %u, 0x%x) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned)bytes, (unsigned)protection, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u32(&out, previous_page);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_free(int fd, reader *r)
{
    int32_t handle;
    uint64_t address;
    uint64_t bytes;
    if (!rd_i32(r, &handle) || !rd_u64(r, &address) || !rd_u64(r, &bytes))
    {
        send_error(fd, 1, "malformed Free request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    uint64_t size = (bytes + ALLOC_GRANULARITY - 1) & ~(ALLOC_GRANULARITY - 1);
    kern_return_t kr = mach_vm_deallocate(slot->task, (mach_vm_address_t)address, (mach_vm_size_t)size);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 10, "mach_vm_deallocate(0x%llx, %llu) failed with kern_return_t %d",
                    (unsigned long long)address, (unsigned long long)size, (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_enumerate(int fd, reader *r)
{
    int32_t handle;
    uint64_t image_start;
    uint64_t image_end;
    if (!rd_i32(r, &handle) || !rd_u64(r, &image_start) || !rd_u64(r, &image_end))
    {
        send_error(fd, 1, "malformed Enumerate request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    buffer records = {0};
    if (collect_writable(slot->task, image_start, image_end, &records) != 0)
    {
        free(records.data);
        send_error(fd, 4, "out of memory");
        return;
    }

    uint32_t count = (uint32_t)(records.len / 17);
    buffer out = {0};
    if (buf_u32(&out, 0) != 0 || buf_u32(&out, count) != 0 || buf_bytes(&out, records.data, records.len) != 0)
    {
        free(records.data);
        free(out.data);
        send_error(fd, 4, "out of memory");
        return;
    }
    free(records.data);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_close(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Close request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot != NULL)
        close_slot(slot);
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_dyld_info(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed DyldInfo request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }

    task_dyld_info_data_t info;
    mach_msg_type_number_t count = TASK_DYLD_INFO_COUNT;
    memset(&info, 0, sizeof(info));
    kern_return_t kr = task_info(slot->task, TASK_DYLD_INFO, (task_info_t)&info, &count);
    if (kr != KERN_SUCCESS)
    {
        send_errorf(fd, 11, "task_info(TASK_DYLD_INFO) failed with kern_return_t %d", (int)kr);
        return;
    }
    buffer out = {0};
    buf_u32(&out, 0);
    buf_u64(&out, (uint64_t)info.all_image_info_addr);
    buf_u64(&out, (uint64_t)info.all_image_info_size);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_pause(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Pause request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    if (slot->paused)
    {
        send_error(fd, 12, "task is already paused");
        return;
    }

    thread_act_array_t threads = NULL;
    mach_msg_type_number_t count = 0;
    kern_return_t kr = task_threads(slot->task, &threads, &count);
    if (kr != KERN_SUCCESS || threads == NULL || count == 0)
    {
        if (threads != NULL)
            mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)threads,
                               (mach_vm_size_t)count * sizeof(thread_act_t));
        send_errorf(fd, 13, "task_threads failed with kern_return_t %d", (int)kr);
        return;
    }

    mach_msg_type_number_t suspended = 0;
    for (; suspended < count; suspended++)
    {
        kr = thread_suspend(threads[suspended]);
        if (kr == KERN_SUCCESS)
            continue;
        for (mach_msg_type_number_t i = 0; i < suspended; i++)
            thread_resume(threads[i]);
        for (mach_msg_type_number_t i = 0; i < count; i++)
            mach_port_deallocate(mach_task_self(), threads[i]);
        mach_vm_deallocate(mach_task_self(), (mach_vm_address_t)threads,
                           (mach_vm_size_t)count * sizeof(thread_act_t));
        send_errorf(fd, 13, "thread_suspend failed with kern_return_t %d", (int)kr);
        return;
    }

    slot->paused = 1;
    slot->threads = threads;
    slot->thread_count = count;

    buffer out = {0};
    buf_u32(&out, 0);
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void op_resume(int fd, reader *r)
{
    int32_t handle;
    if (!rd_i32(r, &handle))
    {
        send_error(fd, 1, "malformed Resume request");
        return;
    }
    task_slot *slot = slot_for(handle);
    if (slot == NULL)
    {
        send_error(fd, 5, "invalid task handle");
        return;
    }
    resume_task(slot);
    buffer out = {0};
    buf_u32(&out, 0);
    send_frame(fd, out.data, (uint32_t)out.len);
    free(out.data);
}

static void handle_request(int fd, int client_id, const uint8_t *request, uint32_t length)
{
    if (length < 1)
    {
        send_error(fd, 1, "empty request");
        return;
    }
    uint8_t opcode = request[0];
    reader r = {request + 1, request + length};
    switch (opcode)
    {
    case OP_OPEN:
        op_open(fd, client_id, &r);
        break;
    case OP_READ:
        op_read(fd, &r);
        break;
    case OP_READ_MANY:
        op_read_many(fd, &r);
        break;
    case OP_WRITE:
        op_write(fd, &r);
        break;
    case OP_ALLOCATE:
        op_allocate(fd, &r);
        break;
    case OP_ALLOCATE_NEAR:
        op_allocate_near(fd, &r);
        break;
    case OP_PROTECT:
        op_protect(fd, &r);
        break;
    case OP_FREE:
        op_free(fd, &r);
        break;
    case OP_ENUMERATE:
        op_enumerate(fd, &r);
        break;
    case OP_CLOSE:
        op_close(fd, &r);
        break;
    case OP_DYLD_INFO:
        op_dyld_info(fd, &r);
        break;
    case OP_PAUSE:
        op_pause(fd, &r);
        break;
    case OP_RESUME:
        op_resume(fd, &r);
        break;
    default:
        send_errorf(fd, 14, "unknown opcode %u", (unsigned)opcode);
        break;
    }
}

static void serve_client(int fd, int client_id)
{
    for (;;)
    {
        uint32_t length = 0;
        if (read_exact(fd, &length, 4) != 0)
            break;
        if (length == 0 || length > MAX_MESSAGE)
            break;
        uint8_t *request = malloc(length);
        if (request == NULL)
            break;
        if (read_exact(fd, request, length) != 0)
        {
            free(request);
            break;
        }
        handle_request(fd, client_id, request, length);
        free(request);
    }
    close_client_tasks(client_id);
}

/* --------------------------------------------------------------- main loop */

static int make_fallback_listener(void)
{
    /* Manual testing only: production starts through launch_activate_socket, so never take over the
       launchd socket path behind launchd's back. */
    const char *path = getenv("FM27_HELPER_SOCKET");
    if (path == NULL)
        return -1;

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0)
        return -1;

    struct sockaddr_un address;
    memset(&address, 0, sizeof(address));
    address.sun_family = AF_UNIX;
    strncpy(address.sun_path, path, sizeof(address.sun_path) - 1);
    unlink(path);
    if (bind(fd, (struct sockaddr *)&address, sizeof(address)) != 0)
    {
        close(fd);
        return -1;
    }
    if (chmod(path, 0666) != 0)
    {
        close(fd);
        return -1;
    }
    if (listen(fd, 8) != 0)
    {
        close(fd);
        return -1;
    }
    return fd;
}

int main(void)
{
    signal(SIGPIPE, SIG_IGN);

    int *launch_fds = NULL;
    size_t launch_count = 0;
    int listeners[8];
    int listener_count = 0;

    if (launch_activate_socket("Fm27Socket", &launch_fds, &launch_count) == 0 &&
        launch_fds != NULL && launch_count > 0)
    {
        for (size_t i = 0; i < launch_count && listener_count < 8; i++)
            listeners[listener_count++] = launch_fds[i];
    }
    else
    {
        int fd = make_fallback_listener();
        if (fd < 0)
        {
            fprintf(stderr, "fm27_helper: not managed by launchd (set FM27_HELPER_SOCKET for manual runs)\n");
            return 1;
        }
        listeners[listener_count++] = fd;
    }

    time_t last_activity = time(NULL);
    int next_client_id = 1;

    for (;;)
    {
        int client = -1;
        int client_id = 0;
        struct pollfd fds[8];
        int nfds = 0;
        for (int i = 0; i < listener_count; i++)
        {
            fds[nfds].fd = listeners[i];
            fds[nfds].events = POLLIN;
            fds[nfds].revents = 0;
            nfds++;
        }

        time_t now = time(NULL);
        if (now - last_activity >= IDLE_SECONDS)
            break;
        int timeout = (int)((IDLE_SECONDS - (now - last_activity)) * 1000);
        if (timeout < 0)
            timeout = 0;

        int ready = poll(fds, (nfds_t)nfds, timeout);
        if (ready < 0)
        {
            if (errno == EINTR)
                continue;
            break;
        }
        if (ready == 0)
        {
            if (time(NULL) - last_activity >= IDLE_SECONDS)
                break;
            continue;
        }

        for (int i = 0; i < nfds; i++)
        {
            if ((fds[i].revents & POLLIN) == 0)
                continue;
            int fd = accept(listeners[i], NULL, NULL);
            if (fd < 0)
                continue;
            client = fd;
            client_id = next_client_id++;
            break;
        }
        if (client < 0)
            continue;

        /* One client at a time; the editor serialises its own requests. */
        serve_client(client, client_id);
        close(client);
        last_activity = time(NULL);
    }

    for (int i = 0; i < listener_count; i++)
        close(listeners[i]);
    if (launch_fds != NULL)
        free(launch_fds);

    for (int i = 0; i < MAX_TASKS; i++)
        close_slot(&g_tasks[i]);
    return 0;
}
