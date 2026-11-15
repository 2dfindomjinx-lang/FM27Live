using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Fm27Editor.App;

namespace Fm27Editor.App.Tests;

public sealed class SectionLifetimeTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference AddUnopenedSection(object owner, string entity)
    {
        var saveData = new object();
        SectionHost.Build(entity,
        [
            new SectionHost.Section("Overview", "Overview", new Panel()),
            new SectionHost.Section("Details", "Details", () => new TextBlock { Tag = saveData }),
        ], "Overview", owner: owner);
        return new WeakReference(saveData);
    }

    static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public void Reset_releases_unopened_section_factories_and_their_old_save_data()
    {
        var owner = new object();
        WeakReference save = null!;
        AvaloniaTestThread.Run(() => save = AddUnopenedSection(owner, "lifetime-factories"));
        Collect();
        Assert.True(save.IsAlive);
        AvaloniaTestThread.Run(() => SectionHost.Release(owner));
        Collect();
        Assert.False(save.IsAlive);
        GC.KeepAlive(owner);
    }

    [Fact]
    public void Reset_preserves_the_selected_section_and_does_not_clear_another_windows_rail()
    {
        AvaloniaTestThread.Run(() =>
        {
            var first = new object();
            var second = new object();
            var oldRail = new ContentControl();
            SectionHost.Build("lifetime-first", [new("Overview", "Overview", new Panel()), new("Details", "Details", new Panel())],
                "Details", railHost: oldRail, owner: first);
            SectionHost.MarkDirty("lifetime-first", ["Details"]);
            SectionHost.Build("lifetime-second", [new("Overview", "Overview", new Panel())], owner: second);
            try
            {
                SectionHost.Release(first);
                Assert.Null(SectionHost.Selected("lifetime-first"));
                Assert.Equal("Overview", SectionHost.Selected("lifetime-second"));
                var newRail = new ContentControl();
                SectionHost.Build("lifetime-first", [new("Overview", "Overview", new Panel()), new("Details", "Details", new Panel())],
                    railHost: newRail, owner: first);
                Assert.Equal("Details", SectionHost.Selected("lifetime-first"));
                Assert.All(((ListBox)newRail.Content!).Items.Cast<object>(), row => Assert.DoesNotContain("*", row.ToString()));
            }
            finally { SectionHost.Release(first); SectionHost.Release(second); }
        });
    }
}
