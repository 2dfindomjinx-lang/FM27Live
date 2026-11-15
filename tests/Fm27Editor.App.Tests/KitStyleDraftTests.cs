using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

/// <summary>
/// Opening Bayern's Kits page (home shirt Item Style 0, Game Pass 27.0.1, 16/11/2026) marked the page and the tab unsaved
/// with nothing edited: the style box raised TextChanged as it loaded and its "0" was clamped to 1. Expected values: the
/// game's own value is no change; a typed value is a change, clamped to the in-game editor's 1..limit.
/// </summary>
public class KitStyleDraftTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData(" 0 ", 0)]
    [InlineData("1", 1)]
    [InlineData("60", 60)]   // a value the game holds above the in-game editor's 56 is still unchanged
    public void The_games_own_style_is_not_a_change(string text, int original) =>
        Assert.Null(MainWindow.KitStyleDraft(text, original, 56));

    [Theory]
    [InlineData("1", 0, 56, "1")]
    [InlineData("12", 1, 56, "12")]
    [InlineData("99", 1, 56, "56")]
    [InlineData("0", 3, 10, "1")]
    [InlineData("20", 3, 15, "15")]
    public void A_typed_style_is_a_change_within_the_editor_range(string text, int original, int limit, string expected) =>
        Assert.Equal(expected, MainWindow.KitStyleDraft(text, original, limit));

    [Fact]
    public void Text_that_is_not_a_number_is_kept_for_the_save_check_to_reject() =>
        Assert.Equal("abc", MainWindow.KitStyleDraft("abc", 1, 56));
}
