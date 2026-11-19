using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class TurkishUiTests : IDisposable
{
    public TurkishUiTests() => LanguageSetting.Set(AppLanguage.Turkish);
    public void Dispose() => LanguageSetting.Set(AppLanguage.English);

    // Turkish football terminology specified for this release; literal expectations are independent of Loc.
    [Theory]
    [InlineData("Free Kick Taking", "Serbest vuruş")]
    [InlineData("Penalty Taking", "Penaltı")]
    [InlineData("Long Throws", "Uzun taçlar")]
    [InlineData("Command of Area", "Ceza sahası hakimiyeti")]
    [InlineData("Judging Player Ability", "Oyuncu yeteneğini değerlendirme")]
    [InlineData("Motivating", "Motivasyon")]
    [InlineData("Preferred Moves", "Tercih edilen hareketler")]
    [InlineData("Wage Budget", "Maaş bütçesi")]
    public void Football_labels_use_the_Turkish_football_glossary(string english, string expected)
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
            var save = new Button { Content = "Değişiklikleri kaydet", IsVisible = false, IsEnabled = false };
            var rail = new ContentControl();
            var body = SectionHost.Build("turkish-save-test",
                [new SectionHost.Section("Contract", "Contract", new StackPanel { Children = { save } })],
                "Contract", null, rail);
            var window = new Window { Content = new StackPanel { Children = { rail, body } } };
            window.Show();
            try
            {
                var list = rail.GetLogicalDescendants().OfType<ListBox>().Single();
                save.IsEnabled = true;
                Assert.Equal("Sözleşme *", list.Items[0]!.ToString());
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
        foreach (var (key, value) in Loc.TurkishTable.Where(pair => pair.Key.Contains('{')))
        {
            // CompositeFormat validates braces and argument indexes independently of Loc.F.
            var english = System.Text.CompositeFormat.Parse(key);
            var turkish = System.Text.CompositeFormat.Parse(value);
            Assert.True(english.MinimumArgumentCount == turkish.MinimumArgumentCount, key);
        }
    }

    [Fact]
    public void Ambiguous_words_keep_their_football_finance_and_colour_meanings()
    {
        // Football balance and financial balance have distinct Turkish meanings.
        Assert.Equal("Denge", Loc.Attribute("Balance"));
        Assert.Equal("Bakiye", Loc.S("Balance"));
        Assert.Equal("Arka plan rengi", Loc.KitColour("Background"));
        Assert.Equal("Ana renk", Loc.KitColour("Foreground"));
        Assert.Equal("Antrenman tesisleri", Loc.ClubField("Training"));
        Assert.Equal("Antrenman", Loc.S("Training"));
        LanguageSetting.Set(AppLanguage.English);
        Assert.Equal("Balance", Loc.Attribute("Balance"));
        Assert.Equal("Background", Loc.KitColour("Background"));
        Assert.Equal("Training", Loc.ClubField("Training"));
    }
}
