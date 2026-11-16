using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class MemoryWriteBatchTests
{
    [Fact]
    public void Overlapping_edits_are_composed_and_rollback_restores_original_bytes()
    {
        for (int failure = 1; failure <= 2; failure++)
        {
            var inner = new FakeMemory(); inner.Map(0x1000, 4).AsSpan().Fill(1); inner.Map(0x2000, 1)[0] = 2;
            var fault = new WriteFaultMemory(inner) { Failure = failure };
            Assert.Throws<MemoryAccessException>(() => MemoryWriteBatch.Run(fault, staged =>
            {
                Assert.True(staged.TryWrite(0x1000, [3, 4, 5, 6]));
                Assert.True(staged.TryWrite(0x1001, [8, 9]));
                Assert.Equal(new byte[] { 3, 8, 9, 6 }, staged.Read(0x1000, 4));
                Assert.True(staged.TryWrite(0x2000, [7]));
            }));
            Assert.Equal(new byte[] { 1, 1, 1, 1 }, inner.Read(0x1000, 4));
            Assert.Equal(2, inner.Read(0x2000, 1)[0]);
        }
    }

    [Fact]
    public void Changed_read_only_guard_prevents_publication()
    {
        var memory = new FakeMemory(); memory.Map(0x1000, 1)[0] = 1; memory.Map(0x2000, 1)[0] = 2;
        var batch = new MemoryWriteBatch(memory);
        batch.Read(0x1000, 1);
        batch.TryWrite(0x2000, [9]);
        memory.TryWrite(0x1000, [3]);
        Assert.Throws<StaleObjectException>(() => batch.Commit());
        Assert.Equal(2, memory.Read(0x2000, 1)[0]);
    }
}
