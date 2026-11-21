using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace Fm27Editor.App.Tests;

public sealed class TypographyTests
{
    [Fact]
    public void Embedded_faces_resolve_without_system_fallback_or_synthetic_bold()
    {
        AvaloniaTestThread.Run(() =>
        {
            var body = new TextBlock { Text = "Çalışkanlık" };
            Assert.Equal("Cabinet Grotesk", new Typeface(body.FontFamily).GlyphTypeface.FamilyName);
            foreach (var (family, weight, expected) in new[]
            {
                (AppTypography.Body, FontWeight.Normal, "Cabinet Grotesk"),
                // The official Medium OTF uses this name ID 1 and shares typographic family ID 16.
                (AppTypography.Body, FontWeight.Medium, "Cabinet Grotesk Medium"),
                (AppTypography.Body, FontWeight.Bold, "Cabinet Grotesk"),
                (AppTypography.PersonName, FontWeight.Normal, "Cabinet Grotesk")
            })
            {
                var face = new Typeface(family, FontStyle.Normal, weight).GlyphTypeface;
                Assert.Equal(expected, face.FamilyName);
                Assert.Equal(weight, face.Weight);
                Assert.Equal(FontSimulations.None, face.FontSimulations);
                foreach (char glyph in "ÇçĞğİıÖöŞşÜü0123456789") Assert.NotEqual((ushort)0, face.GetGlyph(glyph));
            }
        });
    }

    [Fact]
    public void Person_title_uses_regular_while_section_heading_and_rating_use_selected_faces()
    {
        AvaloniaTestThread.Run(() =>
        {
            var person = UiKit.PersonBanner("Faruk Yankayış", null, null);
            var name = person.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Faruk Yankayış");
            Assert.Equal(AppTypography.PersonName, name.FontFamily);
            Assert.Equal(FontWeight.Normal, name.FontWeight);
            var rating = MainWindow.RatingBox(14, 1, 20, _ => { });
            Assert.Equal(AppTypography.Body, rating.FontFamily);
            Assert.Equal(FontWeight.Bold, rating.FontWeight);
            var header = Assert.IsType<TextBlock>(ToolGrid.Header("Attributes", "Attributes"));
            Assert.Equal(AppTypography.Body, header.FontFamily);
            Assert.Equal(FontWeight.Bold, header.FontWeight);
        });
    }
}
