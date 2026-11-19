using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Fm27Editor.Core.Clubs;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Settings;

namespace Fm27Editor.App.Tests;

public class ReviewSafetyTests
{
    [Fact]
    public void Every_known_vision_objective_has_a_Turkish_translation()
    {
        LanguageSetting.Set(AppLanguage.Turkish);
        try
        {
            foreach (var name in ClubVisionTypes.All.Values.Concat(ContractLabels.Terms.Values).Concat(RelationCatalog.All.Select(t => t.Name)))
                Assert.True(name != Loc.S(name), $"Missing Turkish: {name}");
            foreach (var value in Enum.GetValues<PlayingTime>())
            {
                string translated = ContractLabels.Of(value);
                LanguageSetting.Set(AppLanguage.English);
                string english = ContractLabels.Of(value);
                LanguageSetting.Set(AppLanguage.Turkish);
                Assert.True(english != translated, $"Missing Turkish: {english}");
            }
            Assert.NotEqual("Connect to the game to search nations.", Loc.S("Connect to the game to search nations."));
            Assert.Equal("İngiliz sterlini", Loc.S("Pound Sterling"));
            Assert.DoesNotContain("InvalidOperationException", UserMessage.For(new InvalidOperationException("unmapped internal detail")));
        }
        finally { LanguageSetting.Set(AppLanguage.English); }
    }

    [Fact]
    public void Large_term_amounts_cannot_overflow_the_move_now_restriction()
    {
        var terms = new TransferTerms(1_000_000_000, 12, 1_000_000_000, 1, 1_000_000_000, 1, 0, 0, 0, 0, 0, 0, false, 0);
        Assert.True(terms.RequiresScheduledTransfer);
        Assert.False((terms with { Instalments = 0, LeagueApps = 0, InternationalApps = 0, SellOn = 20 }).RequiresScheduledTransfer);
    }

    [Fact]
    public void Unrecorded_undo_path_refuses_a_changed_save()
    {
        var edits = new EditService(null!, null!) { Blocked = () => true };
        bool ran = false;
        Assert.Throws<SaveChangedException>(() => edits.ExecuteUnrecorded(() => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void Percentage_editor_shows_the_value_not_the_unused_parameter()
    {
        Assert.True(ContractLabels.TermHasValue(3));
        Assert.Null(ContractLabels.TermParameter(3));
        Assert.False(ContractTermRules.IsMoney(3));
    }

    [Fact]
    public void Changing_selection_cannot_start_a_second_mass_edit_while_writing()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        MassEditReviewWindow? window = null;
        int calls = 0;
        AvaloniaTestThread.Run(() =>
        {
            var plan = new MassPlan("Nation", 1, [MassRow.Change("England", "Youth Rating", 100, 90, () => 1)]);
            window = new MassEditReviewWindow([plan], (_, _, _) => { calls++; return completion.Task; });
            window.Show();
            var buttons = window.GetLogicalDescendants().OfType<Button>().ToArray();
            var apply = buttons.Single(b => Equals(b.Content, Loc.F("Apply {0}", Loc.S("Nation"))));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, calls);
            var confirm = window.GetLogicalDescendants().OfType<CheckBox>().First();
            confirm.IsChecked = true; // Previously Refresh re-enabled Apply here.
            Assert.False(apply.IsEnabled);
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // Reentry guard must also protect programmatic events.
            Assert.Equal(1, calls);
        });
        completion.SetResult(1);
        AvaloniaTestThread.Run(() => { });
    }
}
