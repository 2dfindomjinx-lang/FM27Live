using Fm27Editor.Core.Game;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Diagnostics;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

public static class UserMessage
{
    /// <summary>The short text shown to the player. The full exception goes to the log, so a bug report shows where it failed.</summary>
    public static string For(Exception error)
    {
        FlightRecorder.Current?.Log("error", error.ToString());
        return Text(error);
    }

    static string Text(Exception error) => error switch
    {
        GameNotRunningException ex => Loc.S(ex.Message),
        SaveChangedException ex => Loc.S(ex.Message),
        UnsupportedBuildException => Loc.S("This game version is not supported"),
        PartialClauseWriteException => Loc.S("Some clause fields may have been saved. Reload before retrying."),
        GameNotReadyException => Loc.S("The game is loading a save or has no save loaded. Wait until it is in game, then press Load Game."),
        StaleObjectException => Loc.S("Game data changed. Reload before retrying."),
        MemoryAccessException => Loc.S("Couldn't access game data. Check that a save is loaded and retry."),
        AggregateException => Loc.S("The edit failed and could not be fully restored. Reload your backup save before continuing."),
        OverflowException or FormatException => Loc.S("Enter a valid number within the allowed range"),
        ArgumentOutOfRangeException => Loc.S("Value is outside the allowed range"),
        System.ComponentModel.Win32Exception ex => Loc.F("Windows error {0}: {1}", ex.NativeErrorCode, ex.Message),
        _ => Loc.TurkishTable.ContainsKey(error.Message) ? Loc.S(error.Message)
            : Loc.IsTr ? Loc.S("The operation could not be completed. See the diagnostic log for details.") : error.Message
    };
}
