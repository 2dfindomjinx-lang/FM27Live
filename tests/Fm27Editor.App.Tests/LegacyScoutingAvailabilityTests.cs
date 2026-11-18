using Avalonia.Controls;
using Avalonia.LogicalTree;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public class LegacyScoutingAvailabilityTests
{
    [Fact]
    public void Uncalibrated_role_and_RCA_columns_are_hidden_but_personality_remains()
    {
        AvaloniaTestThread.Run(() =>
        {
            Assert.False(LegacyScoutingAvailability.Enabled);
            var tab = new TalentedRegensTab(_ => { });
            var grid = tab.Root.GetLogicalDescendants().OfType<DataGrid>().Single();
            var headers = grid.Columns.Select(c => c.Header is Control control
                ? string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Prepend(control as TextBlock).Where(t => t is not null).Select(t => t!.Text))
                : c.Header?.ToString()).ToArray();
            Assert.DoesNotContain(Loc.S("RCA"), headers);
            Assert.DoesNotContain(Loc.S("Role"), headers);
            Assert.DoesNotContain(Loc.S("Role %"), headers);
            Assert.Contains(Loc.S("Personality"), headers);
        });
    }
}
