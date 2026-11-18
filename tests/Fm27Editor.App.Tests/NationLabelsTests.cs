using Avalonia.Controls;
using Avalonia.LogicalTree;
using Fm27Editor.App;
using Fm27Editor.Core.Nations;

namespace Fm27Editor.App.Tests;

public class NationLabelsTests
{
    /// <summary>England (FMRTE): FIFA points 1941, Youth Rating 135, attendance up to 100000; all must be accepted.</summary>
    [Fact]
    public void Accepts_the_values_FMRTE_shows_for_England()
    {
        var changes = new Dictionary<NationField, string>
        {
            [NationField.FifaRankingPoints] = "1941", [NationField.YouthRating] = "135", [NationField.MaximumAttendance] = "100000",
            [NationField.YearAssociationFormed] = "1863", [NationField.Development] = "1",
        };
        Assert.True(NationLabels.TryParse(changes, out var v, out var error));
        Assert.Null(error);
        Assert.Equal(135, v[NationField.YouthRating]);
    }

    [Theory]
    [InlineData(NationField.EconomicFactor, "21")]
    [InlineData(NationField.Development, "-1")]
    [InlineData(NationField.Development, "4")]
    [InlineData(NationField.FifaPosition, "0")]
    [InlineData(NationField.YearAffiliatedToFifa, "19x")]
    public void Any_invalid_value_rejects_the_whole_draft(NationField field, string text)
    {
        var changes = new Dictionary<NationField, string> { [NationField.FaPatience] = "9", [field] = text };
        Assert.False(NationLabels.TryParse(changes, out var v, out _));
        Assert.Empty(v);
    }

    [Fact]
    public void Every_field_is_on_the_screen_once_and_development_codes_are_named()
    {
        var shown = NationLabels.Groups.SelectMany(g => g.Items).Select(i => i.Field).ToList();
        Assert.Equal(Enum.GetValues<NationField>().Length, shown.Count);
        Assert.Equal(shown.Count, shown.Distinct().Count());
        Assert.Equal("Not Set", NationLabels.DevelopmentName(0));
        Assert.Equal("Developed State", NationLabels.DevelopmentName(1));        // England
        Assert.Equal("Developing State", NationLabels.DevelopmentName(2));       // Vietnam
        Assert.Equal("Lower Developed State", NationLabels.DevelopmentName(3));  // Nigeria
        Assert.Equal("Level 4", NationLabels.DevelopmentName(4));
        Assert.Equal("Lower Developed State", NationLabels.FormatValue(NationField.Development, 3));
        Assert.Equal("Very Important", NationLabels.FormatValue(NationField.Importance, 1));
        Assert.Equal("Continuous", NationLabels.FormatValue(NationField.GainNationalityType, 1));
        Assert.Equal("Yes", NationLabels.FormatValue(NationField.DoesNotAllowDualNationality, 1));
        Assert.Equal("15", NationLabels.FormatValue(NationField.EconomicFactor, 15));
        Assert.Equal("Continuous", NationLabels.GainNationalityTypeName(1));
        Assert.Equal("Accumulative", NationLabels.GainNationalityTypeName(2));
        Assert.Equal("Very Important", NationLabels.ImportanceName(1));  // England, Vietnam
        Assert.Equal("No One Cares", NationLabels.ImportanceName(4));    // FMRTE 27 after writing 4 on Spain

        Assert.True(NationLabels.TryParse(new Dictionary<NationField, string> { [NationField.Development] = "0" }, out var zero, out _));
        Assert.Equal(0, zero[NationField.Development]);
        Assert.True(NationLabels.TryParse(new Dictionary<NationField, string> { [NationField.Development] = "3" }, out var three, out _));
        Assert.Equal(3, three[NationField.Development]);
    }

    [Fact]
    public void NationFilterPanel_filters_nations_validates_ranges_and_clears()
    {
        AvaloniaTestThread.Run(() =>
        {
            var england = new NationRecord(0x1000, 765, "England", "ENG");
            var nigeria = new NationRecord(0x2000, 38, "Nigeria", "NGA");
            var panel = new NationFilterPanel([england, nigeria], access: null);

            Assert.Equal(2, panel.TotalCount);
            Assert.Equal(0, panel.FilterCount());
            Assert.Equal("All 2 nations", panel.SummaryStatus());
            Assert.Equal(2, panel.Matched().Count);

            var boxes = panel.GetLogicalDescendants().OfType<TextBox>().ToList();
            var nameBox = boxes.Single(b => b.Watermark == "Name");
            nameBox.Text = "nga";
            Assert.Equal(1, panel.FilterCount());
            Assert.Equal("1 / 2 nations match", panel.SummaryStatus());
            Assert.Equal([nigeria], panel.Matched());

            var minBox = boxes.First(b => b.Watermark == "Min");
            var maxBox = boxes.First(b => b.Watermark == "Max");
            minBox.Text = "20";
            maxBox.Text = "10";
            Assert.Null(panel.Criteria());
            Assert.Equal("Invalid range", panel.SummaryStatus());

            panel.Clear();
            Assert.Equal(0, panel.FilterCount());
            Assert.Equal("All 2 nations", panel.SummaryStatus());
            Assert.Equal(2, panel.Matched().Count);

            var results = new SearchResultsWindow("Nation", [], [], null, nations: [nigeria]);
            Assert.Contains("1 nations", results.Title, StringComparison.Ordinal);
        });
    }
}
