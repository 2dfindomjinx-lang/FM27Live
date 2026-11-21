using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;

namespace Fm27Editor.App;

/// <summary>A local draft preview; the stored CA remains a separate editable field.</summary>
internal sealed class PlayerRcaView : TextBlock
{
    readonly PlayerDraft draft;
    bool subscribed;

    public PlayerRcaView(PlayerDraft draft)
    {
        this.draft = draft;
        FontFamily = AppTypography.Body;
        FontSize = 16;
        FontWeight = FontWeight.Bold;
        Foreground = UiKit.Brush("#4ADE80");
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;
        MinWidth = 34;
        ToolTip.SetTip(this, Loc.S("Recommended Current Ability"));
        Subscribe();
        AttachedToLogicalTree += (_, _) => Subscribe();
        DetachedFromLogicalTree += (_, _) =>
        {
            draft.Changed -= Refresh;
            subscribed = false;
        };
    }

    void Subscribe()
    {
        if (!subscribed) draft.Changed += Refresh;
        subscribed = true;
        Refresh();
    }

    void Refresh() => Text = draft.RecommendedAbility()?.ToString(CultureInfo.InvariantCulture) ?? "—";
}
