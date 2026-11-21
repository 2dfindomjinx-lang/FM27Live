using Avalonia.Controls;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class RoleHighlightTests
{
    [Fact]
    public void Role_selection_marks_key_preferable_and_avoid_without_changing_inputs_or_order()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.English);
            var state = new AttributeRoleSelection();
            var inputs = new[] { new TextBox { Text = "-" }, new TextBox { Text = "17" }, new TextBox { Text = "6" } };
            var grid = new LocalizedAttributeRows("Mental", ["Aggression", "Anticipation", "Bravery"], inputs, state);
            var rows = grid.Children.OfType<Border>().ToArray();
            var positions = rows.Select(Grid.GetRow).ToArray();
            Assert.All(rows, r => Assert.Equal(0, r.BorderThickness.Left));
            state.Select(new PhaseRole("Fixture", "Striker", PossessionPhase.InPossession,
                [new(PlayerAttribute.Aggression, false, true), new(PlayerAttribute.Anticipation, true, false), new(PlayerAttribute.Bravery, false, false)]));
            Assert.Equal(AttributeEmphasis.LowerIsBetter, state.Emphasis("Aggression"));
            Assert.Equal(AttributeEmphasis.Key, state.Emphasis("Anticipation"));
            Assert.Equal(AttributeEmphasis.Preferable, state.Emphasis("Bravery"));
            Assert.Equal(AttributeEmphasis.None, state.Emphasis("CA"));
            Assert.All(rows, r => Assert.Equal(3, r.BorderThickness.Left));
            Assert.Equal(positions, rows.Select(Grid.GetRow));
            Assert.Equal(new[] { "-", "17", "6" }, inputs.Select(i => i.Text));
            state.Select(null);
            Assert.All(rows, r => Assert.Equal(0, r.BorderThickness.Left));
            Assert.Same(inputs[0], rows[0].Child);
        });
    }
}
