/* Run on macOS: clang -O2 -Wall -Wextra tests/Fm27Editor.Helper.Tests/read_many_test.c -o /tmp/fm27-read-many-test && /tmp/fm27-read-many-test
 * Uses this process's own Mach task and a private socket pair; no game, elevation or installed helper required. */
#include <assert.h>
#include <mach/mach.h>
#include <mach/mach_vm.h>
#include <pthread.h>

static int read_calls;
static kern_return_t counted_read(vm_map_read_t task, mach_vm_address_t address, mach_vm_size_t size,
                                  mach_vm_address_t destination, mach_vm_size_t *received)
{
    read_calls++;
    return mach_vm_read_overwrite(task, address, size, destination, received);
}

#define mach_vm_read_overwrite counted_read
#define main fm27_helper_main
#include "../../src/Fm27Editor.Helper/fm27_helper.c"
#undef main
#undef mach_vm_read_overwrite

typedef struct { int fd; buffer *request; } invocation;

static void *invoke(void *argument)
{
    invocation *call = argument;
    reader r = {call->request->data, call->request->data + call->request->len};
    op_read_many(call->fd, &r);
    return NULL;
}

static buffer call(buffer *request)
{
    int sockets[2];
    assert(socketpair(AF_UNIX, SOCK_STREAM, 0, sockets) == 0);
    invocation args = {sockets[0], request};
    pthread_t worker;
    read_calls = 0;
    assert(pthread_create(&worker, NULL, invoke, &args) == 0);
    uint32_t length = 0;
    assert(read_exact(sockets[1], &length, 4) == 0);
    assert(length >= 4 && length <= 40968);
    buffer result = {0};
    assert(buf_reserve(&result, length) == 0);
    result.len = length;
    assert(read_exact(sockets[1], result.data, length) == 0);
    assert(pthread_join(worker, NULL) == 0);
    close(sockets[0]); close(sockets[1]);
    return result;
}

static uint32_t u32(const buffer *b, size_t offset)
{
    assert(offset + 4 <= b->len);
    uint32_t value;
    memcpy(&value, b->data + offset, 4);
    return value;
}

static buffer request(uint32_t handle, uint32_t count)
{
    buffer result = {0};
    assert(buf_u32(&result, handle) == 0 && buf_u32(&result, count) == 0);
    return result;
}

static void entry(buffer *request, const void *address, uint32_t length)
{
    assert(buf_u64(request, (uint64_t)address) == 0 && buf_u32(request, length) == 0);
}

static void error(buffer *request, uint32_t expected)
{
    buffer response = call(request);
    assert(u32(&response, 0) == 1 && u32(&response, 4) == expected);
    assert(read_calls == 0); /* Every field is checked before any potentially valid first read. */
    free(response.data); free(request->data);
}

int main(void)
{
    g_tasks[0].used = 1;
    g_tasks[0].task = mach_task_self();
    uint8_t known[32];
    for (int i = 0; i < 32; i++) known[i] = (uint8_t)(i * 3 + 1);

    buffer req = request(1, 3);
    entry(&req, known, 32); entry(&req, NULL, 8); entry(&req, known, 32);
    buffer response = call(&req);
    assert(u32(&response, 0) == 0 && u32(&response, 4) == 3 && read_calls == 3);
    assert(u32(&response, 8) == 0 && u32(&response, 12) == 32);
    assert(memcmp(response.data + 16, known, 32) == 0);
    assert(u32(&response, 48) == 1 && u32(&response, 52) == 0);
    assert(u32(&response, 56) == 0 && u32(&response, 60) == 32);
    assert(memcmp(response.data + 64, known, 32) == 0 && response.len == 96);
    free(response.data); free(req.data);

    req = request(1, 1024);
    for (int i = 0; i < 1024; i++) entry(&req, known, 32);
    response = call(&req);
    assert(u32(&response, 4) == 1024 && read_calls == 1024 && response.len == 40968);
    for (int i = 0; i < 1024; i++) assert(memcmp(response.data + 16 + i * 40, known, 32) == 0);
    free(response.data); free(req.data);

    req = request(1, 0); error(&req, 1);
    req = request(1, 1025); error(&req, 1);
    req = request(0, 1); entry(&req, known, 1); error(&req, 5);
    req = request(1, 2); entry(&req, known, 1); error(&req, 1); /* Truncated entries. */
    req = request(1, 1); entry(&req, known, 1); buf_u8(&req, 0); error(&req, 1);
    req = request(1, 2); entry(&req, known, 1); entry(&req, known, 0); error(&req, 1);
    req = request(1, 2); entry(&req, known, 1); entry(&req, known, 33); error(&req, 1);
    puts("ReadMany native protocol tests passed");
    return 0;
}
