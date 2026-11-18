// Executes the test-owned x64 cave in an isolated process. Never attaches to FM.
// On Apple silicon, macOS runs this x86_64 executable through Rosetta.
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/mman.h>
#include <unistd.h>
#include <errno.h>
#include <string.h>
typedef int (__attribute__((ms_abi)) *hook_fn)(void *, void *);
int main(int argc, char **argv) {
    if (argc != 5) return 2;
    uintptr_t address = strtoull(argv[2], NULL, 16);
    uintptr_t ctx = strtoull(argv[3], NULL, 16);
    uintptr_t proposal = strtoull(argv[4], NULL, 16);
    const size_t size = 0x10000;
    void *block = mmap((void *)address, size, PROT_READ|PROT_WRITE|PROT_EXEC, MAP_PRIVATE|MAP_ANON, -1, 0);
    if (block == MAP_FAILED || (uintptr_t)block != address) {
        fprintf(stderr, "Cannot map test image at requested address: %s\n", strerror(errno)); return 3;
    }
    FILE *file = fopen(argv[1], "rb");
    if (!file || fread(block, 1, size, file) != size) return 4;
    fclose(file);
    __builtin___clear_cache((char *)block, (char *)block + size);
    hook_fn run = (hook_fn)((char *)block + 0x1000);
    int result = run((void *)ctx, (void *)proposal);
    file = fopen(argv[1], "wb");
    if (!file || fwrite(block, 1, size, file) != size) return 5;
    fclose(file);
    printf("%d\n", result);
    munmap(block, size);
    return 0;
}
