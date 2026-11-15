using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.App;

public sealed partial class MainWindow
{
    /// <summary>"2.5x" for a speed, "normal" for none.</summary>
    static string SpeedText(double multiplier) => multiplier <= 0 ? Loc.S("normal") : $"{multiplier.ToString("0.#", CultureInfo.InvariantCulture)}x";

    /// <summary>Reads "2.5", "2,5" or "2.5x"; 1 or 0 means normal speed (returned as 0). Null when not a number.</summary>
    static double? ParseSpeed(string? text)
    {
        var t = (text ?? "").Trim().TrimEnd('x', 'X').Trim().Replace(',', '.');
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return null;
        return value is 0 or 1 ? 0 : value;
    }

    /// <summary>
    /// "Accelerate Player Development": a speed for the player (or the ticked squad players) that multiplies his training
    /// gains while the game runs. The card shows the speed that is set now; any speed from 1.1x to 20x in steps of 0.1 can
    /// be typed. Writes at once, like the other presets.
    /// </summary>
    void AddDevelopmentSpeed(StackPanel panel, PlayerRecord? player, bool isSquadMode, Func<IReadOnlyList<PlayerRecord>>? getSelectedPlayers,
        Action<string>? onStatus, Action? onApplied, Func<string, Action, bool, Button> actionButton)
    {
        var session = backend.Session;
        if (session is null || !session.CanWrite) return;
        var accel = session.AcceleratedDevelopment;
        var current = new TextBlock { FontSize = 12, Foreground = UiKit.TextSecondary, Text = Loc.S("Speed now: reading…") };
        var speed = new TextBox
        {
            Watermark = Loc.S("Speed, e.g. 2.5"), Height = 24, MinHeight = 24, FontSize = 12.5,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(speed, Loc.F("Any speed from {0}x to {1}x in steps of 0.1; 1 means normal speed.", AcceleratedDevelopment.MinMultiplier.ToString(CultureInfo.InvariantCulture), AcceleratedDevelopment.MaxMultiplier));
        Button? set = null;
        set = actionButton("Set speed", () => _ = SetSpeedAsync(), true);

        panel.Children.Add(new TextBlock
        {
            Text = Loc.S("DEVELOPMENT"), FontSize = 11, FontWeight = FontWeight.Bold, Foreground = UiKit.Brush("#7E8BA3"),
            LetterSpacing = 0.4, Margin = new Thickness(1, 6, 0, 1),
        });
        panel.Children.Add(current);
        panel.Children.Add(speed);
        panel.Children.Add(set);

        IReadOnlyList<PlayerRecord> Targets() => isSquadMode ? getSelectedPlayers?.Invoke() ?? [] : player is null ? [] : [player];

        // The speed set now, read off the UI thread (finding the code site reads the module).
        void ShowCurrent()
        {
            var targets = Targets();
            _ = Task.Run(() =>
            {
                try
                {
                    var rows = accel.Read()?.Players ?? [];
                    return targets.Select(p => rows.FirstOrDefault(r => r.Address == p.Address)?.Multiplier ?? 0).ToList();
                }
                catch { return null; }
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                var speeds = t.Result;
                if (speeds is null) { current.Text = Loc.S("Speed now: unknown"); return; }
                if (speeds.Count == 0) { current.Text = Loc.S("Speed now: no players selected"); return; }
                var distinct = speeds.Distinct().ToList();
                current.Text = distinct.Count == 1
                    ? Loc.F("Speed now: {0}{1}", SpeedText(distinct[0]), speeds.Count > 1 ? Loc.F(" ({0} players)", speeds.Count) : "")
                    : Loc.F("Speed now: mixed ({0})", string.Join(", ", speeds.GroupBy(s => s).OrderByDescending(g => g.Key).Select(g => Loc.F("{0} at {1}", g.Count(), SpeedText(g.Key)))));
                if (distinct.Count == 1 && string.IsNullOrWhiteSpace(speed.Text) && distinct[0] > 0)
                    speed.Text = distinct[0].ToString("0.#", CultureInfo.InvariantCulture);
            }));
        }
        ShowCurrent();

        async Task SetSpeedAsync()
        {
            if (ParseSpeed(speed.Text) is not { } multiplier || multiplier != 0 && !AcceleratedDevelopment.IsAllowedMultiplier(multiplier))
            {
                onStatus?.Invoke(Loc.F("Development speed: enter {0} to {1} with at most one decimal (1 = normal).", AcceleratedDevelopment.MinMultiplier.ToString(CultureInfo.InvariantCulture), AcceleratedDevelopment.MaxMultiplier));
                return;
            }
            var targets = Targets();
            if (targets.Count == 0) { onStatus?.Invoke(Loc.S("No players selected")); return; }
            set!.IsEnabled = false;
            try
            {
                if (multiplier > 0) await Task.Run(() => accel.Install());
                int changed = 0;
                var failed = new List<string>();
                foreach (var p in targets)
                {
                    try
                    {
                        bool did = false;
                        backend.Edits.Execute("Player", p.DisplayName, "Development speed", () => did = accel.Set(p, multiplier));
                        if (did) changed++;
                    }
                    catch (Exception ex) { failed.Add($"{p.DisplayName}: {UserMessage.For(ex)}"); }
                }
                onApplied?.Invoke();
                string who = targets.Count == 1 ? targets[0].DisplayName : Loc.F("{0} player(s)", changed);
                onStatus?.Invoke(failed.Count > 0
                    ? Loc.F("Development speed: {0} set, {1} failed ({2}).", changed, failed.Count, string.Join(", ", failed.Take(2)))
                    : changed == 0
                        ? Loc.F("Development speed: already {0}.", SpeedText(multiplier))
                        : Loc.F("Development speed {0} for {1}. Continue in FM to see updates.", SpeedText(multiplier), who));
            }
            catch (Exception ex) { onStatus?.Invoke(Loc.F("Development speed: {0}", UserMessage.For(ex))); }
            finally { set!.IsEnabled = true; ShowCurrent(); }
        }
    }
}
