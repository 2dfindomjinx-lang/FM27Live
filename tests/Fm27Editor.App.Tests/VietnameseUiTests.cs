using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class VietnameseUiTests : IDisposable
{
    public VietnameseUiTests() => LanguageSetting.Set(AppLanguage.Vietnamese);
    public void Dispose() => LanguageSetting.Set(AppLanguage.English);

    // Independently transcribed label records from the user's ViethoafullFM27.ltc:
    // byte offsets 7910, 7738, 6795, 6056, 8799, 8989, 29924 and 404957.
    [Theory]
    [InlineData("Free Kick Taking", "Đá phạt")]
    [InlineData("Penalty Taking", "Đá phạt đền")]
    [InlineData("Long Throws", "Ném biên")]
    [InlineData("Command of Area", "Khả năng làm chủ vòng cấm")]
    [InlineData("Judging Player Ability", "Đánh giá khả năng cầu thủ")]
    [InlineData("Motivating", "Khả năng tạo động lực")]
    [InlineData("Preferred Moves", "Thói quen ưa thích")]
    [InlineData("Wage Budget", "Ngân sách lương")]
    public void Football_labels_follow_the_supplied_FM_translation(string english, string expected)
    {
        Assert.Equal(expected, Loc.S(english));
        Assert.Equal(expected, Loc.S(english.ToUpperInvariant()));
        LanguageSetting.Set(AppLanguage.English);
        Assert.Equal(english, Loc.S(english));
    }

    [Fact]
    public void A_translated_child_save_still_marks_and_clears_pending_edits()
    {
        AvaloniaTestThread.Run(() =>
        {
            var save = new Button { Content = "Lưu thay đổi", IsVisible = false, IsEnabled = false };
            var rail = new ContentControl();
            var body = SectionHost.Build("vietnamese-save-test",
                [new SectionHost.Section("Contract", "Contract", new StackPanel { Children = { save } })],
                "Contract", null, rail);
            var window = new Window { Content = new StackPanel { Children = { rail, body } } };
            window.Show();
            try
            {
                var list = rail.GetLogicalDescendants().OfType<ListBox>().Single();
                save.IsEnabled = true;
                Assert.Equal("Hợp đồng *", list.Items[0]!.ToString());
                LanguageSetting.Set(AppLanguage.English);
                Assert.Equal("Contract *", list.Items[0]!.ToString());
                save.IsEnabled = false;
                Assert.Equal("Contract", list.Items[0]!.ToString());
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1000)]
    public void Long_contract_labels_fit_their_column_without_losing_text(double width)
    {
        AvaloniaTestThread.Run(() =>
        {
            var form = new UiKit.TwoColumnFormBuilder()
                .AddRow(0, "Minimum Fee Release Clause", new TextBox())
                .AddRow(1, "Wage After Reaching International Caps", new TextBox());
            var grid = form.Grid;
            grid.Measure(new Size(width, double.PositiveInfinity));
            grid.Arrange(new Rect(0, 0, width, grid.DesiredSize.Height));
            foreach (var label in grid.Children.OfType<TextBlock>())
            {
                var full = new TextBlock { Text = label.Text, FontSize = label.FontSize,
                    FontFamily = label.FontFamily, TextWrapping = TextWrapping.Wrap };
                full.Measure(new Size(label.Bounds.Width, double.PositiveInfinity));
                Assert.True(label.Bounds.Height >= full.DesiredSize.Height - 1,
                    $"'{label.Text}' needs {full.DesiredSize.Height}px but has {label.Bounds.Height}px");
                Assert.True(label.Bounds.Width <= 160, "Label must stay in its own column");
            }
        });
    }

    [Fact]
    public void Translation_templates_preserve_the_English_format_arguments()
    {
        foreach (var (key, value) in Loc.VietnameseTable.Where(pair => pair.Key.Contains('{')))
        {
            // CompositeFormat validates braces and argument indexes independently of Loc.F.
            var english = System.Text.CompositeFormat.Parse(key);
            var vietnamese = System.Text.CompositeFormat.Parse(value);
            Assert.True(english.MinimumArgumentCount == vietnamese.MinimumArgumentCount, key);
        }
    }

    [Fact]
    public void Ambiguous_words_keep_their_football_finance_and_colour_meanings()
    {
        // Balance in the supplied attribute block is "Thăng bằng" (byte 5882).
        Assert.Equal("Thăng bằng", Loc.Attribute("Balance"));
        Assert.Equal("Số dư ngân sách", Loc.S("Balance"));
        Assert.Equal("Màu nền", Loc.KitColour("Background"));
        Assert.Equal("Màu chính", Loc.KitColour("Foreground"));
        Assert.Equal("Cơ sở tập luyện", Loc.ClubField("Training"));
        Assert.Equal("Huấn luyện", Loc.S("Training"));
        LanguageSetting.Set(AppLanguage.English);
        Assert.Equal("Balance", Loc.Attribute("Balance"));
        Assert.Equal("Background", Loc.KitColour("Background"));
        Assert.Equal("Training", Loc.ClubField("Training"));
    }
}
