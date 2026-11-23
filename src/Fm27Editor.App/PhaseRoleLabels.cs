namespace Fm27Editor.App;

internal static class PhaseRoleLabels
{
    // Keep the existing personal-dictionary key when the game uses different punctuation.
    internal static string English(string key) => key switch
    {
        "Ball Playing Goalkeeper" => "Ball-Playing Goalkeeper",
        "No Nonsense Goalkeeper" => "No-Nonsense Goalkeeper",
        "Line Holding Keeper" => "Line-Holding Keeper",
        "Centre Back" => "Centre-Back",
        "Ball Playing Centre Back" => "Ball-Playing Centre-Back",
        "No Nonsense Centre Back" => "No-Nonsense Centre-Back",
        "Wide Centre Back" => "Wide Centre-Back",
        "Advanced Centre Back" => "Advanced Centre-Back",
        "Overlapping Centre Back" => "Overlapping Centre-Back",
        "Covering Centre Back" => "Covering Centre-Back",
        "Stopping Centre Back" => "Stopping Centre-Back",
        "Covering Wide Centre Back" => "Covering Wide Centre-Back",
        "Stopping Wide Centre Back" => "Stopping Wide Centre-Back",
        "Full Back" => "Full-Back",
        "Inside Full Back" => "Inside Full-Back",
        "Holding Full Back" => "Holding Full-Back",
        "Pressing Full Back" => "Pressing Full-Back",
        "Inside Wing Back" => "Inside Wing-Back",
        "Playmaking Wing Back" => "Playmaking Wing-Back",
        "Wing Back" => "Wing-Back",
        "Advanced Wing Back" => "Advanced Wing-Back",
        "Holding Wing Back" => "Holding Wing-Back",
        "Pressing Wing Back" => "Pressing Wing-Back",
        "Box To Box Midfielder" => "Box-To-Box Midfielder",
        "Box To Box Playmaker" => "Box-To-Box Playmaker",
        "Deep Lying Playmaker" => "Deep-Lying Playmaker",
        "Deep Lying Forward" => "Deep-Lying Forward",
        _ => key,
    };

    internal static string Display(string id)
    {
        string key = ContractLabels.Spaced(id);
        return Loc.RoleLabel(key, English(key));
    }
}
