using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public sealed class RatingInputTests
{
    [Fact]
    public void First_click_selects_both_digits_typing_replaces_and_second_click_can_edit_one_digit()
    {
        AvaloniaTestThread.Run(() =>
        {
            int staged = 11;
            var box = MainWindow.RatingBox(11, 1, 20, value => staged = value);
            var other = new TextBox { Text = "other" };
            var window = new Window
            {
                Width = 220, Height = 150, ShowActivated = false, ShowInTaskbar = false,
                Position = new PixelPoint(-10000, -10000), WindowStartupLocation = WindowStartupLocation.Manual,
                Content = new StackPanel { Children = { other, box } },
            };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();
                var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true);
                var down = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
                var up = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
                Point PointAt(double x) => presenter.TranslatePoint(new Point(x, presenter.Bounds.Height / 2), window)!.Value;
                void Click(Point point)
                {
                    box.RaiseEvent(new PointerPressedEventArgs(box, pointer, window, point, 1, down, KeyModifiers.None, 1));
                    box.RaiseEvent(new PointerReleasedEventArgs(box, pointer, window, point, 2, up, KeyModifiers.None, MouseButton.Left));
                }
                other.Focus();
                Click(PointAt(presenter.Bounds.Width / 2));
                Assert.Equal(0, Math.Min(box.SelectionStart, box.SelectionEnd));
                Assert.Equal(2, Math.Max(box.SelectionStart, box.SelectionEnd));
                box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "12" });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("12", box.Text);
                Assert.Equal(12, staged);

                // Already focused: a second click places the caret instead of selecting all again.
                box.Focus(NavigationMethod.Pointer);
                Click(PointAt(presenter.Bounds.Width));
                Click(PointAt(presenter.Bounds.Width));
                Assert.Equal(box.SelectionStart, box.SelectionEnd);
                box.CaretIndex = 2;
                box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Back });
                Assert.Equal("1", box.Text);
                box.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "3" });
                Dispatcher.UIThread.RunJobs();
                Assert.Equal("13", box.Text);
                Assert.Equal(13, staged);

                other.Focus();
                box.Focus(NavigationMethod.Tab);
                Assert.Equal(2, Math.Abs(box.SelectionEnd - box.SelectionStart));
                Assert.True(box.Bounds.Width >= 44);
                Assert.True(box.Bounds.Height >= 28);
            }
            finally { window.Close(); }
        });
    }
}
