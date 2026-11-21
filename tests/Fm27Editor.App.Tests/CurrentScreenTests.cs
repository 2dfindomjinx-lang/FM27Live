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
        public int Enumerations;
        public void Pointer(ulong address, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, (int)address);
        public void Int(ulong address, int value) => BitConverter.GetBytes(value).CopyTo(bytes, (int)address);
        public void Copy(ulong from, ulong to, int count) => Array.Copy(bytes, (int)from, bytes, (int)to, count);
        public bool TryRead(ulong address, Span<byte> destination)
        {
            BeforeRead?.Invoke(address, destination.Length);
            if (address + (ulong)destination.Length > (ulong)bytes.Length) return false;
            bytes.AsSpan((int)address, destination.Length).CopyTo(destination); return true;
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> data) => throw new Exception("The screen reader must never write.");
        public IEnumerable<MemoryRegion> EnumerateWritableRegions(ulong imageStart = 0, ulong imageEnd = 0)
        { Enumerations++; yield return new(0x2000, 0x2000, false); }
    }
}
