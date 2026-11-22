using Fm27Editor.Core.Memory;
using Fm27Editor.UnityMetadata;

namespace Fm27Editor.App.Tests;

public sealed class CurrentScreenTests
{
    // Synthetic layout deliberately differs from the installed game: all offsets must come from metadata.
    static readonly ScreenMetadata Layout = new(0x10, 0x18, 0x40, 0x58, 0x68, 0x74, 11, 13, 12, 14, 0x20);
    const ulong Module = 0x1000, Instance = 0x2000, Class = 0x8100, ValueClass = 0x8200, Value = 0x3000;

    [Fact]
    public void Player_to_club_and_non_entity_navigation_never_returns_previous_selection()
    {
        var memory = Create();
        var reader = new CurrentScreenReader(Layout, Module);
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Person, 2002042656), reader.Read(memory));
        memory.Int(Instance + 0x58, 13); memory.Int(Instance + 0x68, 1866);
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Club, 1866), reader.Read(memory));
        memory.Int(Instance + 0x58, 0);
        Assert.Null(reader.Read(memory));
        memory.Int(Instance + 0x58, 13); memory.Pointer(Instance + 0x40, 0);
        Assert.Null(reader.Read(memory));
        memory.Pointer(Instance + 0x40, Value); memory.Int(Instance + 0x68, -1);
        Assert.Null(reader.Read(memory));
        Assert.Equal(1, memory.Enumerations);
    }

    [Fact]
    public void Ambiguous_modules_are_rejected_instead_of_picking_first_match()
    {
        var memory = Create();
        memory.Copy(Instance, 0x2500, 0x100);
        Assert.Null(new CurrentScreenReader(Layout, Module).Read(memory));
    }

    [Fact]
    public void Wrong_typed_value_or_destroyed_native_object_is_not_a_screen()
    {
        var memory = Create();
        memory.Pointer(Value, 0x9900);
        Assert.Null(new CurrentScreenReader(Layout, Module).Read(memory));
        memory.Pointer(Value, ValueClass); memory.Pointer(Instance + 0x20, 0);
        Assert.Null(new CurrentScreenReader(Layout, Module).Read(memory));
    }

    [Fact]
    public void Screen_change_during_read_is_rejected()
    {
        var memory = Create();
        int snapshots = 0;
        memory.BeforeRead = (address, length) =>
        {
            if (address == Instance + 0x40 && length == 8 && ++snapshots == 2) memory.Int(Instance + 0x68, 999);
        };
        Assert.Null(new CurrentScreenReader(Layout, Module).Read(memory));
    }

    [Fact]
    public void Preparation_on_home_screen_caches_module_but_not_entity()
    {
        var memory = Create();
        memory.Int(Instance + 0x58, 0);
        var reader = new CurrentScreenReader(Layout, Module);
        reader.Prepare(memory);
        Assert.True(reader.IsPrepared);
        Assert.Null(reader.Read(memory));
        memory.Int(Instance + 0x58, 13); memory.Int(Instance + 0x68, 1866);
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Club, 1866), reader.Read(memory));
        Assert.Equal(1, memory.Enumerations);
    }

    [Fact]
    public void Freed_page_reenumerates_once_but_persistently_incomplete_scans_are_rejected()
    {
        var memory = Create();
        memory.ReadDenied = (address, size) => memory.Enumerations == 1 && (size > 4096 || address == 0x3000);
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Person, 2002042656), new CurrentScreenReader(Layout, Module).Read(memory));
        Assert.Equal(2, memory.Enumerations);

        memory = Create();
        memory.ReadDenied = (address, size) => size > 4096 || address == 0x3000;
        var reader = new CurrentScreenReader(Layout, Module);
        Assert.Throws<MemoryAccessException>(() => reader.Read(memory));
        Assert.False(reader.IsPrepared);
        Assert.Equal(2, memory.Enumerations);
    }

    [Fact]
    public void Retry_never_accepts_an_ambiguous_or_partially_scanned_result()
    {
        var memory = Create();
        memory.Copy(Instance, 0x2500, 0x100);
        memory.ReadDenied = (address, size) => memory.Enumerations == 1 && (size > 4096 || address == 0x3000);
        Assert.Null(new CurrentScreenReader(Layout, Module).Read(memory));
        Assert.Equal(2, memory.Enumerations);
    }

    [Fact]
    public void Cancelled_discovery_stops_before_reading_or_publishing_a_candidate()
    {
        var memory = Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new CurrentScreenReader(Layout, Module).Prepare(memory, cancellation.Token));
        Assert.Equal(0, memory.Enumerations);
        using var during = new CancellationTokenSource();
        var reader = new CurrentScreenReader(Layout, Module);
        memory.BeforeRead = (_, size) => { if (size > 4096) during.Cancel(); };
        Assert.ThrowsAny<OperationCanceledException>(() => reader.Prepare(memory, during.Token));
        Assert.False(reader.IsPrepared);
    }

    [Fact]
    public async Task Warmup_and_simultaneous_clicks_share_preparation_and_read_fresh_selection()
    {
        var memory = Create();
        int factories = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var session = new CurrentScreenSession(() =>
        {
            Interlocked.Increment(ref factories); entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            return new CurrentScreenReader(Layout, Module);
        }, () => memory);
        Task prepared = session.PrepareAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Task<CurrentScreenTarget> first = session.ReadAsync(), second = session.ReadAsync();
        release.Set();
        await prepared;
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Person, 2002042656), await first);
        Assert.Equal(await first, await second);
        memory.Int(Instance + 0x58, 13); memory.Int(Instance + 0x68, 1866);
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Club, 1866), await session.ReadAsync());
        Assert.Equal(1, factories);
        Assert.Equal(1, memory.Enumerations);
        memory.Int(Instance + 0x58, 0);
        Assert.Null(await session.ReadAsync());
        Assert.Equal(1, memory.Enumerations);
    }

    [Fact]
    public async Task Failed_preparation_retains_metadata_and_disposed_session_never_returns_a_target()
    {
        var memory = Create();
        memory.ReadDenied = (_, size) => size >= 4096;
        int factories = 0;
        using var session = new CurrentScreenSession(() => { factories++; return new CurrentScreenReader(Layout, Module); }, () => memory);
        await Assert.ThrowsAsync<MemoryAccessException>(() => session.PrepareAsync());
        memory.ReadDenied = null;
        Assert.NotNull(await session.ReadAsync());
        Assert.Equal(1, factories);
        session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ReadAsync());
    }

    [Fact]
    public async Task Navigation_transition_retries_a_known_module_without_a_new_heap_pass()
    {
        var memory = Create();
        using var session = new CurrentScreenSession(() => new CurrentScreenReader(Layout, Module), () => memory);
        await session.PrepareAsync();
        int snapshots = 0;
        memory.BeforeRead = (address, size) =>
        {
            if (address == Instance + 0x40 && size == 8 && ++snapshots == 2)
            { memory.Int(Instance + 0x58, 13); memory.Int(Instance + 0x68, 1866); }
        };
        Assert.Equal(new CurrentScreenTarget(ScreenEntityKind.Club, 1866), await session.ReadAsync());
        Assert.Equal(1, memory.Enumerations);
    }

    static TestMemory Create()
    {
        var memory = new TestMemory();
        memory.Pointer(Module + 0x10, Class); memory.Pointer(Module + 0x18, ValueClass);
        memory.Pointer(Instance, Class); memory.Pointer(Instance + 0x20, 0x4000);
        memory.Pointer(Instance + 0x40, Value); memory.Pointer(Value, ValueClass);
        memory.Int(Instance + 0x58, 11); memory.Int(Instance + 0x68, 2002042656); memory.Int(Instance + 0x74, 42);
        return memory;
    }

    sealed class TestMemory : IProcessMemory, IMemoryRegions
    {
        readonly byte[] bytes = new byte[0x10000];
        public Action<ulong, int>? BeforeRead;
        public Func<ulong, int, bool>? ReadDenied;
        public int Enumerations;
        public void Pointer(ulong address, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, (int)address);
        public void Int(ulong address, int value) => BitConverter.GetBytes(value).CopyTo(bytes, (int)address);
        public void Copy(ulong from, ulong to, int count) => Array.Copy(bytes, (int)from, bytes, (int)to, count);
        public bool TryRead(ulong address, Span<byte> destination)
        {
            BeforeRead?.Invoke(address, destination.Length);
            if (ReadDenied?.Invoke(address, destination.Length) == true) return false;
            if (address + (ulong)destination.Length > (ulong)bytes.Length) return false;
            bytes.AsSpan((int)address, destination.Length).CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new Exception("The screen reader must never write.");
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        { Enumerations++; yield return new(0x2000, 0x2000, false); }
    }
}
