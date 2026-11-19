namespace Fm27Editor.App.Live;

public static class OverlayLayout
{
    public const double HandleWidth = 112, HandleHeight = 40;
    public static (double Width, double Height, double MaxWidth, double MaxHeight) Fit(double screenWidth, double screenHeight, double wantedWidth, double wantedHeight)
    {
        double maxWidth = screenWidth * 0.78, maxHeight = screenHeight * 0.82;
        double width = Math.Clamp(double.IsFinite(wantedWidth) ? wantedWidth : 760, Math.Min(420, maxWidth), maxWidth);
        double height = Math.Clamp(double.IsFinite(wantedHeight) ? wantedHeight : 640, Math.Min(300, maxHeight), maxHeight);
        return (width, height, maxWidth, maxHeight);
    }
}
