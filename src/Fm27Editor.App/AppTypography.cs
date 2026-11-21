using Avalonia.Media;

namespace Fm27Editor.App;

internal static class AppTypography
{
    public const string BodySource = "avares://Fm27Editor/Assets/Fonts/Cabinet#Cabinet Grotesk";
    public static readonly FontFamily Body = new(BodySource);
    public static readonly FontFamily PersonName = Body;
}
