using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Fm27Editor.App;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App.Tests;

public class PlayerRcaPreviewTests
{
    static PlayerRecord Player(byte raw = 50)
    {
        var positions = Enumerable.Repeat((byte)1, 14).ToArray();
        positions[(int)PlayerPosition.Striker] = 20;
        return new PlayerRecord
        {
            Address = 1, VtableRva = 1, CurrentAbility = 127, PotentialAbility = 160,
            HomeReputation = 0, CurrentReputation = 0, WorldReputation = 0, Height = 180,
            AttributeScale = 5, RawAttributes = Enumerable.Repeat(raw, 54).ToArray(),
            Positions = positions, Personality = new byte[8],
        };
    }
    static FieldDescriptor<PlayerRecord> Field(string id) => PlayerFields.All.Single(f => f.Id == id);

    [Fact]
    public void Typed_attribute_updates_RCA_before_blur_and_invalid_text_preserves_last_draft()
    {
        AvaloniaTestThread.Run(() =>
        {
            var player = Player();
            var draft = new PlayerDraft(player);
            var value = new PlayerRcaView(draft);
            var field = Field("Finishing");
            var box = MainWindow.RatingBox(10, 1, 20, n => draft.Stage(field, n), () => draft.Value(field));
            Assert.Equal("79", value.Text); // Independent native-engine fixture.
            box.Text = "20";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("97", value.Text);
            Assert.Equal(1, draft.Count);
            Assert.Equal(50, player.Raw(PlayerAttribute.Finishing));
            Assert.Equal(127, player.CurrentAbility);
            box.Text = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("97", value.Text);
            box.Text = "999";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(20, draft.Value(field));
            box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Assert.Equal("20", box.Text);
            box.Text = "10";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("79", value.Text);
            Assert.Equal(0, draft.Count);
        });
    }

    [Fact]
    public void Draft_includes_position_feet_and_CA_edits_but_preserves_unedited_raw_precision()
    {
        var player = Player();
        player.RawAttributes[(int)PlayerAttribute.Finishing] = 53;
        player.RawAttributes[(int)PlayerAttribute.Pace] = 66;
        var draft = new PlayerDraft(player);
        Assert.Equal(84, draft.RecommendedAbility());
        draft.Stage(Field("PA"), 200);
        Assert.Equal(84, draft.RecommendedAbility());
        draft.Stage(Field("CA"), 10);
        Assert.Equal(10, draft.RecommendedAbility());
        Assert.Equal(127, player.CurrentAbility);
        draft = new PlayerDraft(Player());
        draft.Stage(Field("LeftFoot"), 20);
        draft.Stage(Field("RightFoot"), 20);
        Assert.Equal(97, draft.RecommendedAbility());
        draft = new PlayerDraft(Player());
        draft.Stage(Field("DefenderCentral"), 20);
        draft.Stage(Field("MidfielderCentral"), 18);
        Assert.Equal(91, draft.RecommendedAbility());
    }

    [Fact]
    public void Rebase_preserves_pending_edits_and_uses_new_clean_values()
    {
        var draft = new PlayerDraft(Player());
        draft.Stage(Field("Finishing"), 20);
        var fresh = Player();
        fresh.RawAttributes[(int)PlayerAttribute.Pace] = 66;
        var rebased = draft.Rebase(fresh);
        Assert.Equal(20, rebased.Value(Field("Finishing")));
        Assert.Equal(66, rebased.Original.Raw(PlayerAttribute.Pace));
        Assert.Equal(50, fresh.Raw(PlayerAttribute.Finishing));
        Assert.Equal(1, rebased.Count);
        Assert.NotEqual(draft.RecommendedAbility(), rebased.RecommendedAbility());
    }
}
