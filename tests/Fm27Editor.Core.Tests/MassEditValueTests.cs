using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public sealed class MassEditValueTests
{
    [Theory]
    [InlineData(70, -100, 0)]
    [InlineData(70, 1000, 200)]
    [InlineData(70, -10, 60)]
    public void Change_by_saturates_to_the_field_range(long before, long delta, long expected) =>
        Assert.Equal(expected, MassEditValue.Resolve(MassEditMode.Change, before, delta, 0, 200));

    [Theory]
    [InlineData(long.MaxValue, long.MaxValue, 200)]
    [InlineData(long.MinValue, long.MinValue, 0)]
    public void Extreme_deltas_cannot_overflow(long before, long delta, long expected) =>
        Assert.Equal(expected, MassEditValue.Resolve(MassEditMode.Change, before, delta, 0, 200));

    [Fact]
    public void Set_to_stays_strict_and_rejects_out_of_range_values() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MassEditValue.Resolve(MassEditMode.Set, 70, -1, 0, 200));
}
