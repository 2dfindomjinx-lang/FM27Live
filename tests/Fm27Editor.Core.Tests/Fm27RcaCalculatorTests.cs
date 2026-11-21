using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class Fm27RcaCalculatorTests
{
    // Independent expected results: synthetic inputs evaluated with FM27's native
    // calculation in an isolated emulator, not with the implementation under test.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(5, 1)]
    [InlineData(30, 1)]
    [InlineData(50, 79)]
    [InlineData(80, 199)]
    [InlineData(100, 200)]
    public void Uniform_attributes_match_native_engine(byte raw, int expected)
    {
        var (attributes, positions) = Inputs(raw);
        Assert.Equal(expected, Fm27RcaCalculator.Calculate(attributes, positions, 127));
    }

    [Theory]
    [InlineData(20, 79)]
    [InlineData(18, 79)]
    [InlineData(17, 78)]
    [InlineData(15, 76)]
    [InlineData(12, 70)]
    [InlineData(10, 63)]
    [InlineData(2, 1)]
    public void Familiarity_matches_native_engine(byte rating, int expected)
    {
        var (attributes, positions) = Inputs(50);
        positions[(int)PlayerPosition.Striker] = rating;
        Assert.Equal(expected, Fm27RcaCalculator.Calculate(attributes, positions, 127));
    }

    [Theory]
    [InlineData(PlayerAttribute.Finishing, 100, 97)]
    [InlineData(PlayerAttribute.Pace, 100, 95)]
    public void Nonlinear_attribute_cost_matches_native_engine(PlayerAttribute attribute, byte raw, int expected)
    {
        var (attributes, positions) = Inputs(50);
        attributes[(int)attribute] = raw;
        Assert.Equal(expected, Fm27RcaCalculator.Calculate(attributes, positions, 127));
    }

    [Fact]
    public void Feet_versatility_and_fractional_attributes_match_native_engine()
    {
        var (attributes, positions) = Inputs(50);
        attributes[(int)PlayerAttribute.LeftFoot] = attributes[(int)PlayerAttribute.RightFoot] = 100;
        Assert.Equal(97, Fm27RcaCalculator.Calculate(attributes, positions, 127));
        (attributes, positions) = Inputs(50);
        positions[(int)PlayerPosition.DefenderCentral] = 20;
        positions[(int)PlayerPosition.MidfielderCentral] = 18;
        Assert.Equal(91, Fm27RcaCalculator.Calculate(attributes, positions, 127));
        (attributes, positions) = Inputs(50);
        attributes[(int)PlayerAttribute.Finishing] = 53;
        attributes[(int)PlayerAttribute.Pace] = 66;
        Assert.Equal(84, Fm27RcaCalculator.Calculate(attributes, positions, 127));
        Assert.Equal(10, Fm27RcaCalculator.Calculate(attributes, positions, 10));
    }

    [Fact]
    public void Missing_or_invalid_inputs_are_unknown_and_inputs_are_never_modified()
    {
        var (attributes, positions) = Inputs(50);
        var savedAttributes = attributes.ToArray();
        var savedPositions = positions.ToArray();
        _ = Fm27RcaCalculator.Calculate(attributes, positions, 127);
        Assert.Equal(savedAttributes, attributes);
        Assert.Equal(savedPositions, positions);
        Assert.Null(Fm27RcaCalculator.Calculate([], positions, 127));
        Assert.Null(Fm27RcaCalculator.Calculate(attributes, [], 127));
        Assert.Null(Fm27RcaCalculator.Calculate(attributes, positions, 0));
        Assert.Null(Fm27RcaCalculator.Calculate(new byte[attributes.Length], positions, 127));
        Array.Fill(positions, (byte)1);
        Assert.Null(Fm27RcaCalculator.Calculate(attributes, positions, 127));
        positions[(int)PlayerPosition.Striker] = 21;
        Assert.Null(Fm27RcaCalculator.Calculate(attributes, positions, 127));
    }

    static (byte[], byte[]) Inputs(byte raw)
    {
        var attributes = Enumerable.Repeat(raw, Enum.GetValues<PlayerAttribute>().Length).ToArray();
        var positions = Enumerable.Repeat((byte)1, Enum.GetValues<PlayerPosition>().Length).ToArray();
        positions[(int)PlayerPosition.Striker] = 20;
        return (attributes, positions);
    }
}
