using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class AttributeOrderTests : IDisposable
{
    public void Dispose() => AvaloniaTestThread.Run(() =>
    {
        AttributeOrderSetting.Set(false);
        LanguageSetting.Set(AppLanguage.English);
    });

    static LocalizedAttributeRows Card(string group)
    {
        var fields = PlayerFields.All.Where(f => f.Group == group).ToArray();
        var card = MainWindow.AttributeCard(group, fields.Select((f, i) => new TextBox { Name = f.Id, Text = (i + 1).ToString() }), rowIds: fields.Select(f => f.Id).ToArray());
        return card.GetLogicalDescendants().OfType<LocalizedAttributeRows>().Single();
    }

    static string[] Order(LocalizedAttributeRows grid) => grid.Children.OfType<Border>()
        .OrderBy(Grid.GetRow).Select(row => row.Child!.Name!).ToArray();

    // Transcribed from the user's Turkish FM screenshot, with feet retained below the eight physical ratings.
    [Theory]
    [InlineData("Technical", "Finishing,Dribbling,FirstTouch,Heading,Marking,Crossing,Passing,Technique,Tackling,LongShots,Corners,PenaltyTaking,FreeKicks,LongThrows")]
    [InlineData("Mental", "Aggression,Bravery,WorkRate,Decisions,Determination,Concentration,Leadership,Anticipation,Flair,Positioning,Composure,Teamwork,OffTheBall,Vision")]
    [InlineData("Physical", "Agility,Stamina,Balance,Strength,JumpingReach,Pace,Acceleration,NaturalFitness,LeftFoot,RightFoot")]
    public void Turkish_default_matches_the_game_screenshot(string group, string expected)
    {
        AvaloniaTestThread.Run(() =>
        {
            AttributeOrderSetting.Set(false);
            LanguageSetting.Set(AppLanguage.Turkish);
            var grid = Card(group);
            Assert.Equal(expected.Split(','), Order(grid));
            grid.Measure(new Size(240, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, 240, grid.DesiredSize.Height));
            Assert.Equal(expected.Split(','), grid.Children.OfType<Border>().OrderBy(row => row.Bounds.Y).Select(row => row.Child!.Name));
            if (group == "Technical")
            {
                var heading = Assert.Single(grid.Children.OfType<TextBlock>());
                Assert.True(heading.IsVisible);
                Assert.Equal("Duran toplar", heading.Text);
                Assert.Equal(10, Grid.GetRow(heading));
                Assert.Equal(11, Grid.GetRow(grid.Children.OfType<Border>().Single(r => r.Child!.Name == "Corners")));
            }
        });
    }

    [Theory]
    [InlineData("Technical")]
    [InlineData("Mental")]
    [InlineData("Physical")]
    [InlineData("Goalkeeping")]
    [InlineData("Hidden")]
    [InlineData("Personality")]
    public void English_and_English_override_keep_the_original_field_order(string group)
    {
        AvaloniaTestThread.Run(() =>
        {
            var original = PlayerFields.All.Where(f => f.Group == group).Select(f => f.Id).ToArray();
            LanguageSetting.Set(AppLanguage.English);
            AttributeOrderSetting.Set(false);
            var grid = Card(group);
            Assert.Equal(original, Order(grid));
            AttributeOrderSetting.Set(true);
            LanguageSetting.Set(AppLanguage.Turkish);
            Assert.Equal(original, Order(grid));
            Assert.All(grid.Children.OfType<TextBlock>(), heading => Assert.False(heading.IsVisible));
            LanguageSetting.Set(AppLanguage.English);
            Assert.Equal(original, Order(grid));
        });
    }

    [Fact]
    public void Live_reordering_keeps_controls_parents_and_uncommitted_edits()
    {
        AvaloniaTestThread.Run(() =>
        {
            LanguageSetting.Set(AppLanguage.English);
            AttributeOrderSetting.Set(false);
            var grid = Card("Technical");
            var borders = grid.Children.OfType<Border>().ToArray();
            var controls = borders.Select(b => (TextBox)b.Child!).ToArray();
            controls[0].Text = "-";
            controls[3].Text = "19";
            Dispatcher.UIThread.RunJobs();
            int edits = 0, focusLosses = 0;
            foreach (var input in controls)
            {
                input.TextChanged += (_, _) => edits++;
                input.LostFocus += (_, _) => focusLosses++;
            }
            for (int i = 0; i < 3; i++)
            {
                LanguageSetting.Set(AppLanguage.Turkish);
                AttributeOrderSetting.Set(true);
                AttributeOrderSetting.Set(false);
                LanguageSetting.Set(AppLanguage.English);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("-", controls[0].Text);
            Assert.Equal("19", controls[3].Text);
            for (int i = 0; i < controls.Length; i++)
            {
                Assert.Same(controls[i], borders[i].Child);
                Assert.Same(borders[i], controls[i].Parent);
                Assert.Same(grid, borders[i].Parent);
            }
            Assert.Equal(0, edits);
            Assert.Equal(0, focusLosses);
        });
    }

    [Fact]
    public void Preference_survives_reload_and_unknown_values_default_to_language()
    {
        var previous = AttributeOrderSetting.SettingPathOverride;
        var path = Path.Combine(Path.GetTempPath(), "fm27-order-" + Guid.NewGuid() + ".txt");
        try
        {
            AttributeOrderSetting.SettingPathOverride = path;
            AttributeOrderSetting.Load();
            Assert.False(AttributeOrderSetting.UseEnglishOrder);
            AttributeOrderSetting.Set(true);
            Assert.Equal("english", File.ReadAllText(path));
            AttributeOrderSetting.Load();
            Assert.True(AttributeOrderSetting.UseEnglishOrder);
            AttributeOrderSetting.Set(false);
            Assert.Equal("language", File.ReadAllText(path));
            AttributeOrderSetting.Load();
            Assert.False(AttributeOrderSetting.UseEnglishOrder);
            File.WriteAllText(path, "invalid");
            AttributeOrderSetting.Load();
            Assert.False(AttributeOrderSetting.UseEnglishOrder);
        }
        finally { AttributeOrderSetting.SettingPathOverride = previous; File.Delete(path); }
    }
}
