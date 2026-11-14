using Avalonia.Controls;
using Fm27Editor.App;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public sealed class MoveNowDateTests : IDisposable
{
    public void Dispose() => DateSetting.Set(DateLayout.YearMonthDay);

    [Fact]
    public void Unticking_move_now_uses_tomorrow_in_the_chosen_format_and_reads_the_current_game_date()
    {
        DateSetting.Set(DateLayout.DayMonthYear);
        AvaloniaTestThread.Run(() =>
        {
            DateOnly? today = new DateOnly(2026, 12, 31);
            var moveNow = new CheckBox { IsChecked = true };
            var date = new DateBox();
            MoveNowDate.Bind(moveNow, date, () => today);

            moveNow.IsChecked = false;
            Assert.Equal("01-01-2027", date.Text);
            date.Text = "05-01-2027";
            moveNow.IsChecked = true;
            Assert.Equal("05-01-2027", date.Text);
            today = new DateOnly(2027, 1, 19);
            moveNow.IsChecked = false;
            Assert.Equal("20-01-2027", date.Text);
        });
    }

    [Fact]
    public void Unticking_when_the_game_date_is_unavailable_does_not_use_the_computer_date()
    {
        AvaloniaTestThread.Run(() =>
        {
            var moveNow = new CheckBox { IsChecked = true };
            var date = new DateBox { Text = "2027-03-01" };
            MoveNowDate.Bind(moveNow, date, () => null);
            moveNow.IsChecked = false;
            Assert.Equal("", date.Text);
        });
    }
}
