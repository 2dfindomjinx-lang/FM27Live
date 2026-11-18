using Fm27Editor.Core.Game;
using Fm27Editor.Core.Memory;
using Fm27Editor.Core.Offsets;

namespace Fm27Editor.Core.Tests;

/// <summary>
/// The game's GAME_UI save count and the player list guard every write. Game Pass 27.1.0 has the static at
/// module+0x53FBA40 and the u64 count at GAME_UI+0x100 (read on 18/11/2026); a fake profile places them at addresses
/// this test can map. The expected behaviour is FMLibreEditor's: a moved count stays refused until a new session, and
/// an empty player list (main menu) refuses without becoming sticky.
/// </summary>
public class GameLoadStateTests
{
    const ulong GameUi = 0x4000_0000;
    static readonly uint GameUiVtable = TestWorld.PlayerVtable;

    static OffsetProfile ProfileWithGameUi()
    {
        var text = File.ReadAllText(TestWorld.ProfilePath);
        int at = text.IndexOf("\"buildStringRva\"", StringComparison.Ordinal);
        int lineEnd = text.IndexOf('\n', at);
        Assert.True(at >= 0 && lineEnd > at, "buildStringRva line not found");
        text = text.Insert(lineEnd, "\n  \"gameUiRva\": \"0x5703900\",\n  \"gameUiVtable\": \"0x4D27228\",\n  \"gameUiLoadsOffset\": \"0x100\",");
        return OffsetProfile.Load(text);
    }

    static (TestWorld World, OffsetProfile Profile, GameLoadState State) Setup(ulong loads = 3)
    {
        var world = new TestWorld();
        var profile = ProfileWithGameUi();
        TestWorld.Set64(world.Memory.Map(TestWorld.ModuleBase + 0x5703900, 8), 0, GameUi);
        var ui = world.Memory.Map(GameUi, 0x200);
        TestWorld.Set64(ui, 0, TestWorld.ModuleBase + GameUiVtable);
        BitConverter.GetBytes(loads).CopyTo(ui, 0x100);
        return (world, profile, new GameLoadState(world.Memory, profile, TestWorld.ModuleBase));
    }

    [Fact]
    public void A_stable_count_and_a_loaded_save_allow_writes()
    {
        var (_, _, state) = Setup();
        state.EnsureWritable();
        Assert.False(state.SaveChanged);
    }

    [Fact]
    public void A_moved_count_blocks_and_stays_blocked_until_a_new_session()
    {
        var (world, profile, state) = Setup();
        Assert.True(world.Memory.TryWrite(GameUi + 0x100, BitConverter.GetBytes(4UL)));
        Assert.Throws<GameNotReadyException>(state.EnsureWritable);
        Assert.True(state.SaveChanged);

        // Putting the old value back does not clear a load that already happened.
        Assert.True(world.Memory.TryWrite(GameUi + 0x100, BitConverter.GetBytes(3UL)));
        Assert.Throws<GameNotReadyException>(state.EnsureWritable);

        // Load Game creates a new session: its baseline is the game's current count.
        var fresh = new GameLoadState(world.Memory, profile, TestWorld.ModuleBase);
        fresh.EnsureWritable();
        Assert.False(fresh.SaveChanged);
    }

    [Fact]
    public void An_empty_player_list_blocks_but_clears_when_the_save_appears()
    {
        var (world, profile, state) = Setup();
        ulong root = TestWorld.ModuleBase + profile.PlayerListRootRva;
        ulong begin = world.Memory.ReadUInt64(root + (ulong)profile.PlayerListBeginOffset);
        Assert.True(world.Memory.TryWrite(root + (ulong)profile.PlayerListEndOffset, BitConverter.GetBytes(begin)));
        Assert.Throws<GameNotReadyException>(state.EnsureWritable);

        Assert.True(world.Memory.TryWrite(root + (ulong)profile.PlayerListEndOffset, BitConverter.GetBytes(begin + 8)));
        state.EnsureWritable();
    }

    [Fact]
    public void A_profile_without_game_ui_falls_back_to_the_player_list()
    {
        var world = new TestWorld();
        var state = new GameLoadState(world.Memory, world.Profile, TestWorld.ModuleBase);
        state.EnsureWritable();
        ulong root = TestWorld.ModuleBase + world.Profile.PlayerListRootRva;
        ulong begin = world.Memory.ReadUInt64(root + (ulong)world.Profile.PlayerListBeginOffset);
        Assert.True(world.Memory.TryWrite(root + (ulong)world.Profile.PlayerListEndOffset, BitConverter.GetBytes(begin)));
        Assert.Throws<GameNotReadyException>(state.EnsureWritable);
    }

    [Fact]
    public void A_missing_or_wrong_game_ui_object_blocks()
    {
        var (world, profile, _) = Setup();
        Assert.True(world.Memory.TryWrite(GameUi, BitConverter.GetBytes(0UL)));   // wrong vtable
        Assert.Throws<GameNotReadyException>(new GameLoadState(world.Memory, profile, TestWorld.ModuleBase).EnsureWritable);
        Assert.True(world.Memory.TryWrite(TestWorld.ModuleBase + 0x5703900, BitConverter.GetBytes(0UL)));   // static pointer cleared
        Assert.Throws<GameNotReadyException>(new GameLoadState(world.Memory, profile, TestWorld.ModuleBase).EnsureWritable);
    }

    [Fact]
    public void A_session_installs_the_gate_on_its_memory()
    {
        var (world, profile, _) = Setup();
        using var session = new GameSession(world.Memory, new GameProcess(1, TestWorld.ModuleBase, 0x100000, "27", null), profile);
        Assert.True(world.Memory.TryWrite(TestWorld.ListArray, new byte[] { 1 }));
        Assert.True(world.Memory.TryWrite(GameUi + 0x100, BitConverter.GetBytes(4UL)));
        Assert.Throws<GameNotReadyException>(() => world.Memory.TryWrite(TestWorld.ListArray, new byte[] { 2 }));
    }
}
