using Fm27Editor.Core.Game;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public static class UserMessage
{
    public static string For(Exception error) => error switch
    {
        GameNotRunningException ex => Loc.S(ex.Message),
        SaveChangedException ex => Loc.S(ex.Message),
        UnsupportedBuildException => Loc.S("This game version is not supported"),
        PartialClauseWriteException => Loc.S("Some clause fields may have been saved. Reload before retrying."),
        StaleObjectException => Loc.S("Player data changed in game; reloaded"),
        MemoryAccessException => Loc.S("Couldn't access game data. Check that a save is loaded and retry."),
        ArgumentOutOfRangeException => Loc.S("Value is outside the allowed range"),
        System.ComponentModel.Win32Exception ex => Loc.F("Windows error {0}: {1}", ex.NativeErrorCode, ex.Message),
        _ => $"{error.GetType().Name}: {error.Message}"
    };
}
