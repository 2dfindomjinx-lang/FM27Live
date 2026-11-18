using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Game;

/// <summary>
/// The game's own count of saves loaded since it started, and its player list, read before every write. FM keeps the
/// count in its one <c>game_plugin::GAME_UI</c> object (profile <c>gameUiRva</c>/<c>gameUiLoadsOffset</c>). A count that
/// moved since this session attached means every address the editor read may belong to another save, so writes stay
/// refused until Load Game creates a new session. An empty or unreadable player list (main menu, before a save is
/// loaded) blocks too, but not stickily: it clears when the save appears. A profile without the GAME_UI fields uses
/// only the player-list check.
/// </summary>
public sealed class GameLoadState
{
    readonly IProcessMemory memory;
    readonly OffsetProfile profile;
    readonly ulong moduleBase;
    readonly object gate = new();
    ulong? loads;
    bool changed;

    public GameLoadState(IProcessMemory memory, OffsetProfile profile, ulong moduleBase)
    {
        this.memory = memory;
        this.profile = profile;
        this.moduleBase = moduleBase;
        Refresh();
    }

    /// <summary>True once the save count moved since this session read it; stays true until a new session.</summary>
    public bool SaveChanged { get { lock (gate) return changed; } }

    /// <summary>Re-reads the save count; a moved count makes <see cref="SaveChanged"/> true. Unreadable gives nothing.</summary>
    public void Refresh()
    {
        lock (gate)
        {
            if (ReadLoads() is not { } now) return;
            if (loads is null) loads = now;
            else if (now != loads) changed = true;
        }
    }

    /// <summary>False while the player list is empty or unreadable: the main menu, a new game, or a save still loading.</summary>
    public bool HasSave()
    {
        Span<byte> q = stackalloc byte[8];
        ulong root = moduleBase + profile.PlayerListRootRva;
        if (!memory.TryRead(root + (ulong)profile.PlayerListBeginOffset, q)) return false;
        ulong begin = BitConverter.ToUInt64(q);
        if (!memory.TryRead(root + (ulong)profile.PlayerListEndOffset, q)) return false;
        ulong end = BitConverter.ToUInt64(q);
        return begin != 0 && end > begin;
    }

    /// <summary>Called before every write; throws <see cref="GameNotReadyException"/> while the game is loading or has no save.</summary>
    public void EnsureWritable()
    {
        lock (gate)
        {
            bool confirmed = true;
            if (profile.GameUiRva is not null)
            {
                if (ReadLoads() is { } now)
                {
                    if (loads is null) loads = now;
                    else if (now != loads) changed = true;
                }
                else confirmed = false;   // the game's object cannot be read: refuse rather than write blindly
            }
            if (changed || !confirmed || !HasSave()) throw new GameNotReadyException();
        }
    }

    /// <summary>The game's count, or null when the profile does not place it or the object cannot be checked.</summary>
    ulong? ReadLoads()
    {
        if (profile.GameUiRva is not { } rva || profile.GameUiLoadsOffset is not { } offset) return null;
        Span<byte> q = stackalloc byte[8];
        if (!memory.TryRead(moduleBase + rva, q)) return null;
        ulong gameUi = BitConverter.ToUInt64(q);
        if (gameUi == 0) return null;
        if (profile.GameUiVtable is { } vtable)
        {
            if (!memory.TryRead(gameUi, q) || BitConverter.ToUInt64(q) != moduleBase + vtable) return null;
        }
        if (!memory.TryRead(gameUi + (ulong)offset, q)) return null;
        return BitConverter.ToUInt64(q);
    }
}

/// <summary>A write was refused because the game is loading a save, has no save loaded, or has loaded another one.</summary>
public sealed class GameNotReadyException()
    : Exception("The game is loading a save or has no save loaded; nothing was written.");
