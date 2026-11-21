using Avalonia.Media;

namespace Fm27Editor.App;

internal static class AppTypography
{
    public const string BodySource = "avares://Fm27Editor/Assets/Fonts/Cabinet#Cabinet Grotesk";
    public static readonly FontFamily Body = new(BodySource);
    public static readonly FontFamily PersonName = Body;
    public const double BodySize = 15;
    public const double HeadingSize = 14;
    public static readonly FontWeight BodyWeight = FontWeight.Medium;
}
