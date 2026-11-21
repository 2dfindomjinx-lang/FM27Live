namespace Fm27Editor.Core.Players;

/// <summary>
/// FM27's iterative recommended ability calculation. Evaluates staged values locally;
/// it never calls into, writes to, or needs a running game. Native-engine comparisons
/// cover the nonlinear correction, positional versatility, feet and familiarity.
/// Kept separate from the older FMCET scouting formula.
/// </summary>
public static class Fm27RcaCalculator
{
    // Evaluation order is significant near a rounding boundary.
    static readonly PlayerAttribute[] EvaluationOrder =
    [
        PlayerAttribute.Crossing, PlayerAttribute.Dribbling, PlayerAttribute.Finishing,
        PlayerAttribute.Heading, PlayerAttribute.LongShots, PlayerAttribute.Marking,
        PlayerAttribute.OffTheBall, PlayerAttribute.Passing, PlayerAttribute.PenaltyTaking,
        PlayerAttribute.Tackling, PlayerAttribute.Vision, PlayerAttribute.Handling,
        PlayerAttribute.AerialReach, PlayerAttribute.CommandOfArea, PlayerAttribute.Communication,
        PlayerAttribute.Kicking, PlayerAttribute.Throwing, PlayerAttribute.Anticipation,
        PlayerAttribute.Decisions, PlayerAttribute.OneOnOnes, PlayerAttribute.Positioning,
        PlayerAttribute.Reflexes, PlayerAttribute.FirstTouch, PlayerAttribute.Technique,
        PlayerAttribute.LeftFoot, PlayerAttribute.RightFoot, PlayerAttribute.Flair,
        PlayerAttribute.Corners, PlayerAttribute.Teamwork, PlayerAttribute.WorkRate,
        PlayerAttribute.LongThrows, PlayerAttribute.Eccentricity, PlayerAttribute.RushingOut,
        PlayerAttribute.TendencyToPunch, PlayerAttribute.Acceleration, PlayerAttribute.FreeKicks,
        PlayerAttribute.Strength, PlayerAttribute.Stamina, PlayerAttribute.Pace,
        PlayerAttribute.JumpingReach, PlayerAttribute.Leadership, PlayerAttribute.Dirtiness,
        PlayerAttribute.Balance, PlayerAttribute.Bravery, PlayerAttribute.Consistency,
        PlayerAttribute.Aggression, PlayerAttribute.Agility, PlayerAttribute.ImportantMatches,
        PlayerAttribute.InjuryProneness, PlayerAttribute.Versatility, PlayerAttribute.NaturalFitness,
        PlayerAttribute.Determination, PlayerAttribute.Composure, PlayerAttribute.Concentration,
    ];

    static readonly PlayerPosition[] AllPositions = Enum.GetValues<PlayerPosition>();
    static readonly double[][] PositionWeights = AllPositions.Select(position =>
        EvaluationOrder.Select(attribute => RcaCalculator.WeightFor(position, attribute)).ToArray()).ToArray();

    // Formation position flags, indexed by PlayerPosition (not memory offsets).
    static readonly int[] PositionFlags = [1, 8, 16, 4, 128, 512, 1024, 256, 4096, 8192, 2048, 16384, 64, 32];

    public static int? Calculate(PlayerRecord player) => player.AttributeScale == 5
        ? Calculate(player.RawAttributes, player.Positions, player.CurrentAbility) : null;

    public static int? Calculate(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> positions, int currentAbility)
    {
        if (!Valid(attributes, positions, currentAbility)) return null;
        int? best = null;
        foreach (var position in AllPositions)
        {
            if (positions[(int)position] <= 1) continue;
            int? value = CalculateForPosition(attributes, positions, currentAbility, position);
            if (value is { } candidate && (best is null || candidate > best)) best = candidate;
        }
        return best;
    }

    public static int? CalculateForPosition(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> positions,
        int currentAbility, PlayerPosition position, bool adjustForFamiliarity = true)
    {
        if (!Valid(attributes, positions, currentAbility) || !Enum.IsDefined(position)) return null;
        Span<double> weights = stackalloc double[EvaluationOrder.Length];
        PositionWeights[(int)position].CopyTo(weights);
        int left = attributes[(int)PlayerAttribute.LeftFoot], right = attributes[(int)PlayerAttribute.RightFoot];
        if (left == 100 && right < 100) weights[24] = 0;
        if (right == 100) weights[25] = 0;

        double weightSum = 0, total = 0, divisor = 0;
        int weightCount = 0;
        for (int i = 0; i < weights.Length; i++)
        {
            double weight = weights[i];
            if (weight == 0) continue;
            weightSum += weight;
            weightCount++;
            int raw = attributes[(int)EvaluationOrder[i]];
            if (raw == 0) continue;
            total += raw * weight;
            divisor += weight;
        }
        if (weightCount == 0 || divisor == 0) return null;
        double threshold = weightSum / weightCount * 3.75;
        int versatility = PositionCost(positions);
        int current = currentAbility <= 10 ? 10 : 0;
        int previous = -1000;
        for (int iteration = 0; currentAbility > 10 && iteration <= 100; iteration++)
        {
            double expected = 0, correction = 0;
            if (current > 0)
            {
                // FM deliberately uses float arithmetic for this intermediate value.
                expected = (double)((current - Math.Min(current / 4, versatility / 3)) / 20f + 6f) * 5;
                double above = 0, below = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    int raw = attributes[(int)EvaluationOrder[i]];
                    if (weights[i] == 0 || raw == 0) continue;
                    double deviation = (raw - expected) * weights[i] / 5;
                    if (deviation > threshold) above += deviation - threshold;
                    else if (deviation < -threshold) below += -deviation - threshold;
                }
                correction = (above - below) * 3.75;
            }
            double average = (total + correction) / divisor;
            if (current == 0)
            {
                current = Math.Max(1, (int)((average / 5 - 6) * 20));
                continue;
            }
            double difference = expected - average;
            difference += difference >= 0 ? 0.5 : -0.5;
            int step = (int)difference;
            if (difference > 0 && step == 0) step = 1;
            int bound = 5 - Math.Min(iteration / 10, 4);
            step = Math.Clamp(step, -bound, bound);
            if (previous >= -999 && step != 0)
            {
                if (Math.Abs(previous - step) > 5 && Math.Abs(step) > 1) step /= 2;
                if (Math.Abs(step) > Math.Abs(previous)) step = Math.Abs(previous) * Math.Sign(step);
                if (step + previous == 0 && Math.Abs(step) != 1) step -= Math.Sign(step);
                if (previous > 0 && step > previous)
                {
                    current--;
                    continue;
                }
                if (previous < 0) step = Math.Max(previous, step);
            }
            previous = step;
            if (step == 0) break;
            current += step < 0 ? 1 : -1;
        }

        if (adjustForFamiliarity)
        {
            int familiarity = 100, decrement = 1;
            for (int missing = 1; missing <= 18 - positions[(int)position]; missing++)
            {
                if (missing % 3 == 0) decrement *= 2;
                familiarity = Math.Max(1, familiarity - decrement);
            }
            current = (int)(current * familiarity / 100.0 + 0.5);
        }
        return Math.Clamp(current, 1, 200);
    }

    static bool Valid(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> positions, int currentAbility)
    {
        if (attributes.Length < EvaluationOrder.Length || positions.Length < AllPositions.Length || currentAbility is < 1 or > 200)
            return false;
        foreach (var value in attributes[..EvaluationOrder.Length]) if (value > 100) return false;
        foreach (var value in positions[..AllPositions.Length]) if (value > 20) return false;
        return true;
    }

    static int PositionCost(ReadOnlySpan<byte> positions)
    {
        int total = 0, flags = 0;
        for (int i = 0; i < AllPositions.Length; i++)
        {
            total += Math.Max(positions[i] - 10, 0);
            if (positions[i] >= 15) flags |= PositionFlags[i];
        }
        int Has(int group) => (flags & group) != 0 ? 1 : 0;
        int rows = Has(1) + Has(0x1c) + Has(0xe0) + Has(0x700) + Has(0x3800) + Has(0x1c000);
        int middle = Has(0xe0) + Has(0x700) + Has(0x3800);
        int sides = Has(0x8924) + Has(0x11248) + Has(0x6490);
        int lines = Has(1) + Has(0x1c) + Has(0x1c000) + (middle > 0 ? 1 : 0);
        if (Has(1) != 0 && lines >= 2) lines += 2;
        if (Has(0x1c000) != 0 && Has(0x1c) != 0) lines = Math.Max(lines, 3);
        if (Has(0x3800) != 0 && Has(0xe0) != 0) lines = Math.Max(lines, 2);
        int cost = total - (middle >= 2 ? 3 : 0) + (rows + sides) * 3 - (Has(0x19b6c) != 0 ? 3 : 0);
        if (lines > 1) cost += lines * lines * (lines + 1);
        return Math.Clamp(cost, 40, 140) - 40;
    }
}
