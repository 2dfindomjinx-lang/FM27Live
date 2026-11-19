using Fm27Editor.App.Live;

namespace Fm27Editor.App.Tests;

public sealed class OverlayLayoutTests
{
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1366, 768)]
    [InlineData(960, 540)] // 1920 x 1080 at 200% display scaling
    [InlineData(640, 360)]
    public void EvenLegacyFullscreenPreferencesLeaveGameVisible(double width, double height)
    {
        var panel = OverlayLayout.Fit(width, height, 3840, 2160);
        Assert.True(panel.Width < width * 0.8);
        Assert.True(panel.Height < height * 0.85);
        Assert.True(panel.Width > 0 && panel.Height > 0);
        Assert.True(OverlayLayout.HandleWidth < 120 && OverlayLayout.HandleHeight <= 40);
    }
    [Fact]
    public void InvalidSavedDimensionsCannotCreateAnUnboundedWindow()
    {
        var panel = OverlayLayout.Fit(1920, 1080, double.NaN, double.PositiveInfinity);
        Assert.True(double.IsFinite(panel.Width) && double.IsFinite(panel.Height));
        Assert.True(panel.Width < 1920 && panel.Height < 1080);
    }
}
