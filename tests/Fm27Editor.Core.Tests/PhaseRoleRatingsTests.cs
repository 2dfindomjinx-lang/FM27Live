using System.Text.Json;
using Fm27Editor.Core.Players;

namespace Fm27Editor.Core.Tests;

public class PhaseRoleRatingsTests
{
    static PhaseRoleRatings Catalog()
    {
        var path = Path.GetTempFileName();
        try
        {
            var attrs = new object[] { new { Name = "Finishing", KeyAttribute = true, Avoid = false }, new { Name = "Passing", KeyAttribute = false, Avoid = false }, new { Name = "Workrate", KeyAttribute = false, Avoid = true } };
            File.WriteAllText(path, JsonSerializer.Serialize(new {
                GeneralPositionToRoleAndDutiesDefinitionsMap = new { Striker = new[] { new { Role = "CentreForward", DutiesAttributes = new { InPossession = attrs, OutOfPossession = attrs } } } },
                KeyAttributesWeight = 16, PreferableAttributesWeight = 4, AvoidAttributesWeight = 3,
                PositionRatingPenalties = Enumerable.Range(1, 20).Select(n => n == 18 ? 2 : 0).ToArray(), SettingsVersion = "test"
            }));
            return PhaseRoleRatings.Load(path);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Group_weights_inverse_attribute_and_position_penalty_are_distinct()
    {
        var world = new TestWorld();
        var player = new PlayerRepository(world.Memory, world.Profile, TestWorld.ModuleBase).Reload(TestWorld.Mainoo)!;
        player.RawAttributes[(int)PlayerAttribute.Finishing] = 100;
        player.RawAttributes[(int)PlayerAttribute.Passing] = 50;
        player.RawAttributes[(int)PlayerAttribute.WorkRate] = 25;
        player.Positions[(int)PlayerPosition.Striker] = 18;
        var c = Catalog();
        // (20*16 + 10*4 + (20-5)*3)/23 *5 *0.98
        Assert.Equal(86.28260869565217, c.Score(player, PlayerPosition.Striker, c.At(PlayerPosition.Striker, PossessionPhase.InPossession).Single())!.Value, 9);
        Assert.Single(c.At(PlayerPosition.Striker, PossessionPhase.OutOfPossession));
        Assert.Empty(c.At(PlayerPosition.AttackingMidLeft, PossessionPhase.InPossession));
        player.RawAttributes[(int)PlayerAttribute.WorkRate] = 0;
        Assert.Null(c.Score(player, PlayerPosition.Striker, c.Roles[0]));
    }
}
