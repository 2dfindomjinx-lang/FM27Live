using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

/// <summary>The role/duty weights are part of the exe, not a file next to it that a player has to import.</summary>
public class RoleCatalogTests
{
    [Fact]
    public void The_role_catalog_is_embedded_in_the_app_assembly()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("Fm27Editor.Ratings.rolesAndDuties.json");
        Assert.NotNull(stream);
        var catalog = PhaseRoleRatings.Load(stream!);
        Assert.NotEmpty(catalog.Roles);
        Assert.Contains(catalog.Roles, r => r.Phase == PossessionPhase.InPossession);
        Assert.Contains(catalog.Roles, r => r.Phase == PossessionPhase.OutOfPossession);
    }
}
