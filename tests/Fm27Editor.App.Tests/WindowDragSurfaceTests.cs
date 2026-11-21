using Avalonia.Controls;
using Fm27Editor.App.Live;

namespace Fm27Editor.App.Tests;

public sealed class WindowDragSurfaceTests
{
    [Fact]
    public void Whole_header_background_drags_but_buttons_and_inputs_are_excluded()
    {
        AvaloniaTestThread.Run(() =>
        {
            var title = new TextBlock { Text = "FM27 Live" };
            var button = new Button { Content = "Collapse" };
            var input = new TextBox();
            var header = new StackPanel { Children = { title, button, input } };
            Assert.False(WindowDragSurface.IsInteractive(header, header));
            Assert.False(WindowDragSurface.IsInteractive(title, header));
            Assert.True(WindowDragSurface.IsInteractive(button, header));
            Assert.True(WindowDragSurface.IsInteractive(input, header));
        });
    }
}
