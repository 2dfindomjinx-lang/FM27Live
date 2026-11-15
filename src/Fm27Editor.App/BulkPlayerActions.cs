using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Fm27Editor.Core.Players;
using Fm27Editor.Core.Presets;

namespace Fm27Editor.App;

public sealed record BulkActionReport(int Changed, int Unchanged, IReadOnlyList<(string Player, string Error)> Failures)
{
    public string FormatStatus(string actionLabel)
    {
        if (Failures.Count > 0)
        {
            var listed = string.Join("; ", Failures.Take(3).Select(f => $"{f.Player}: {f.Error}"));
            if (Failures.Count > 3) listed += $", +{Failures.Count - 3}";
            return Loc.F("{0}: {1:N0} ok, {2:N0} failed ({3})", actionLabel, Changed, Failures.Count, listed);
        }
        if (Changed == 0) return Loc.F("Nothing changed ({0:N0} checked)", Unchanged);
        return Unchanged > 0
            ? Loc.F("{0}: applied to {1:N0}, {2:N0} unchanged", actionLabel, Changed, Unchanged)
            : Loc.F("{0}: applied to {1:N0} players", actionLabel, Changed);
    }
}

public sealed partial class MainWindow
{
    /// <summary>
    /// Runs <paramref name="action"/> across <paramref name="targets"/> inside a single history/undo group.
    /// Returns true from <paramref name="action"/> when something changed (or use <see cref="RunBulkVoid"/> when writes track change count).
    /// </summary>
    internal static BulkActionReport RunBulk(IEditService edits, IReadOnlyList<PlayerRecord> targets, string actionTitle,
        Func<PlayerRecord, bool> action, Action<PlayerRecord>? registerLabels = null)
    {
        var distinct = targets.DistinctBy(p => p.Address).ToList();
        int changed = 0, unchanged = 0;
        var failures = new List<(string Player, string Error)>();
        using (edits.Group("Players", Loc.S("Bulk Edit"), actionTitle))
        {
            foreach (var player in distinct)
            {
                try
                {
                    bool dirty = false;
                    registerLabels?.Invoke(player);
                    edits.Execute("Player", player.DisplayName, actionTitle, () => dirty = action(player));
                    if (dirty) changed++;
                    else unchanged++;
                }
                catch (Exception ex)
                {
                    failures.Add((player.DisplayName, ex.Message));
                }
            }
        }
        return new BulkActionReport(changed, unchanged, failures);
    }

    internal static BulkActionReport RunBulkVoid(IEditService edits, IReadOnlyList<PlayerRecord> targets, string actionTitle,
        Action<PlayerRecord> action, Action<PlayerRecord>? registerLabels = null) =>
        RunBulk(edits, targets, actionTitle, player =>
        {
            long before = edits.WriteCount;
            action(player);
            return before < 0 || edits.WriteCount > before;
        }, registerLabels);

    internal static IReadOnlyList<PlayerRecord> SelectedPlayersFrom(DataGrid grid, Func<object?, PlayerRecord?>? unwrap = null)
    {
        unwrap ??= item => item as PlayerRecord;
        var list = new List<PlayerRecord>();
        foreach (var item in grid.SelectedItems)
            if (unwrap(item) is { } p) list.Add(p);
        if (list.Count == 0 && unwrap(grid.SelectedItem) is { } single) list.Add(single);
        return list.DistinctBy(p => p.Address).ToList();
    }

    void AttachPlayerContextMenu(DataGrid targetGrid, Func<object?, PlayerRecord?> unwrap, Action? onAfter = null)
    {
        // Right-clicking a row outside the current selection selects that row alone; right-clicking inside preserves Shift/Ctrl selection.
        targetGrid.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(targetGrid).Properties.IsRightButtonPressed) return;
            if (e.Source is not Visual v) return;
            if (v.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not null) return;
            var row = v.FindAncestorOfType<DataGridRow>(includeSelf: true);
            if (row is null || unwrap(row.DataContext) is not { } clicked) return;
            var current = SelectedPlayersFrom(targetGrid, unwrap);
            if (!current.Any(p => p.Address == clicked.Address))
                targetGrid.SelectedItem = row.DataContext;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var menu = new ContextMenu();
        menu.Opening += (_, e) =>
        {
            var targets = SelectedPlayersFrom(targetGrid, unwrap);
            if (targets.Count == 0) { e.Cancel = true; return; }
            BuildPlayerContextMenuItems(menu, targets, onAfter);
        };
        targetGrid.ContextMenu = menu;
    }

    void BuildPlayerContextMenuItems(ContextMenu menu, IReadOnlyList<PlayerRecord> targets, Action? onAfter)
    {
        menu.Items.Clear();
        bool connected = backend.State.Status == ConnectionStatus.Connected;
        bool writable = connected && backend.Session.Process.CanWrite;

        var header = new MenuItem { Header = Loc.F("Actions ({0:N0} selected)", targets.Count), IsEnabled = false };
        menu.Items.Add(header);

        var open = new MenuItem { Header = Loc.S("Open Player"), IsEnabled = targets.Count == 1 };
        open.Click += (_, _) => { if (targets.Count >= 1) ShowDetailTab(targets[0]); };
        menu.Items.Add(open);

        if (!connected) return;
        menu.Items.Add(new Separator());

        void RunVoid(string key, Action<PlayerRecord> op) => ExecuteGridBulk(key, targets, p =>
        {
            long before = backend.Edits.WriteCount;
            op(p);
            return before < 0 || backend.Edits.WriteCount > before;
        }, onAfter);

        void RunBool(string key, Func<PlayerRecord, bool> op) => ExecuteGridBulk(key, targets, op, onAfter);

        MenuItem Item(string key, Action click, bool enabled = true)
        {
            var m = new MenuItem { Header = Loc.S(key), IsEnabled = enabled };
            m.Click += (_, _) => click();
            return m;
        }

        menu.Items.Add(Item("Prevent retirement", () =>
        {
            var session = backend.Session;
            var writer = new KeepPlayingWriter(session.Memory, session.Profile, session.ModuleBase);
            RunBool("Prevent retirement", p =>
            {
                if (writer.TryPlan(p) is not { } plan) return false;
                writer.Apply(plan);
                return true;
            });
        }, writable));

        menu.Items.Add(Item("Freeze", () =>
        {
            var freezer = backend.Freezer;
            if (freezer is null) return;
            var report = RunBulk(backend.Edits, targets, Loc.S("Freeze"), p =>
            {
                freezer.SetPlayer(p, FrozenPlayer.AllAttributes(p));
                return true;
            }, RegisterPlayerLabels);
            FinishBulkUi(Loc.S("Freeze"), targets, report, reloadGameData: false, onAfter);
        }, writable && backend.Freezer is not null));

        menu.Items.Add(Item("Unfreeze", () =>
        {
            var freezer = backend.Freezer;
            if (freezer is null) return;
            var report = RunBulk(backend.Edits, targets, Loc.S("Unfreeze"), p =>
            {
                if (p.Id is not > 0 || !freezer.Players.Any(f => f.Id == p.Id.Value)) return false;
                freezer.RemovePlayer(p.Id.Value);
                return true;
            }, RegisterPlayerLabels);
            FinishBulkUi(Loc.S("Unfreeze"), targets, report, reloadGameData: false, onAfter);
        }, writable && backend.Freezer is not null));

        var stateMenu = new MenuItem { Header = Loc.S("State"), IsEnabled = writable };
        stateMenu.Items.Add(Item("Heal", () => RunVoid("Heal", p => backend.Session.Heal(p)), writable));
        stateMenu.Items.Add(Item("Inspire", () => RunVoid("Inspire", p => backend.Session.Inspire(p)), writable));
        stateMenu.Items.Add(Item("Destroy", () => RunVoid("Destroy", p => backend.Session.Destroy(p)), writable));
        stateMenu.Items.Add(Item("Remove bans", () => RunVoid("Remove bans", p => backend.Session.RemoveMatchBans(p)), writable));
        menu.Items.Add(stateMenu);

        var presetsMenu = new MenuItem { Header = Loc.S("Presets"), IsEnabled = writable };
        presetsMenu.Items.Add(Item("Remove Int. Withdrawal", () => RunBool("Remove Int. Withdrawal", p => backend.Session.RemoveInternationalWithdrawal(p) > 0), writable));
        presetsMenu.Items.Add(Item("Register for UEFA", () => RunBool("Register for UEFA", p => backend.Session.RegisterForUefa(p)), writable));
        presetsMenu.Items.Add(Item("Register for Domestic", () => RunBool("Register for Domestic", p => backend.Session.RegisterForDomestic(p)), writable));
        presetsMenu.Items.Add(Item("Trained at club", () => RunBool("Trained at club", p => backend.Session.TrainedAtCurrentClub(p)), writable));
        presetsMenu.Items.Add(Item("Trained at nation", () => RunBool("Trained at nation", p => backend.Session.TrainedInCurrentNation(p)), writable));
        presetsMenu.Items.Add(Item("Improve Team Cohesion", () => RunBool("Improve Team Cohesion", p => backend.Session.SetTeamCohesion(p, TeamCohesionWriter.Improve)), writable));
        presetsMenu.Items.Add(Item("Tactical Understanding", () => RunVoid("Tactical Understanding", p => backend.Session.TacticalUnderstanding(p)), writable));
        presetsMenu.Items.Add(Item("All-in-one", () => RunVoid("All-in-one", p => backend.Session.AllInOne(p)), writable));
        menu.Items.Add(presetsMenu);

        var contractMenu = new MenuItem { Header = Loc.S("Contract Actions"), IsEnabled = writable };
        contractMenu.Items.Add(Item("Renew for 4 Years", () => RunVoid("Renew for 4 Years", p => SquadAdapter.RenewForYears(backend.Session, p, 4)), writable));
        contractMenu.Items.Add(Item("Make it Expire", () => RunVoid("Make it Expire", p => SquadAdapter.MakeExpire(backend.Session, p)), writable));
        contractMenu.Items.Add(Item("−1 Year", () => RunVoid("−1 Year", p => SquadAdapter.ShiftExpiry(backend.Session, p, -1)), writable));
        contractMenu.Items.Add(Item("+1 Year", () => RunVoid("+1 Year", p => SquadAdapter.ShiftExpiry(backend.Session, p, 1)), writable));
        menu.Items.Add(contractMenu);

        var transferMenu = new MenuItem { Header = Loc.S("Transfer Status"), IsEnabled = writable };
        void SetTransfer(string labelKey, TransferListing value) => RunBool(labelKey, p =>
        {
            var cw = new ContractWriter(backend.Session.Memory, backend.Session.Profile, backend.Session.ModuleBase);
            var d = cw.Read(p) ?? throw new InvalidOperationException(Loc.S("No active contract"));
            if (d.Transfer == value) return false;
            cw.SetTransferListing(p, d, value);
            return true;
        });
        transferMenu.Items.Add(Item("Transfer: None", () => SetTransfer("Transfer: None", TransferListing.None), writable));
        transferMenu.Items.Add(Item("Transfer: Listed", () => SetTransfer("Transfer: Listed", TransferListing.Listed), writable));
        transferMenu.Items.Add(Item("Transfer: By Request", () => SetTransfer("Transfer: By Request", TransferListing.ByRequest), writable));
        transferMenu.Items.Add(Item("Transfer: Not for Sale", () => SetTransfer("Transfer: Not for Sale", TransferListing.NotForSale), writable));
        menu.Items.Add(transferMenu);

        var loanMenu = new MenuItem { Header = Loc.S("Loan Status"), IsEnabled = writable };
        void SetLoan(string labelKey, LoanListing value) => RunBool(labelKey, p =>
        {
            var cw = new ContractWriter(backend.Session.Memory, backend.Session.Profile, backend.Session.ModuleBase);
            var d = cw.Read(p) ?? throw new InvalidOperationException(Loc.S("No active contract"));
            if (d.Loan == value) return false;
            cw.SetLoanListing(p, d, value);
            return true;
        });
        loanMenu.Items.Add(Item("Loan: None", () => SetLoan("Loan: None", LoanListing.None), writable));
        loanMenu.Items.Add(Item("Loan: Listed", () => SetLoan("Loan: Listed", LoanListing.Listed), writable));
        loanMenu.Items.Add(Item("Loan: Not for Loan", () => SetLoan("Loan: Not for Loan", LoanListing.NotForLoan), writable));
        menu.Items.Add(loanMenu);

        var speedMenu = new MenuItem { Header = Loc.S("Development Speed"), IsEnabled = writable };
        speedMenu.Items.Add(Item("Normal (1x)", () => _ = ApplyBulkDevelopmentSpeedAsync(targets, 0, onAfter), writable));
        foreach (double m in new[] { 1.5, 2.0, 3.0, 5.0, 10.0 })
        {
            double val = m;
            var mi = new MenuItem { Header = $"{val.ToString("0.#", CultureInfo.InvariantCulture)}x", IsEnabled = writable };
            mi.Click += (_, _) => _ = ApplyBulkDevelopmentSpeedAsync(targets, val, onAfter);
            speedMenu.Items.Add(mi);
        }
        speedMenu.Items.Add(new Separator());
        speedMenu.Items.Add(Item("Custom…", () => _ = PromptBulkDevelopmentSpeedAsync(targets, onAfter), writable));
        menu.Items.Add(speedMenu);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Mass Edit…", () => OpenMassEditForPlayers(targets), writable));
    }

    void ExecuteGridBulk(string actionKey, IReadOnlyList<PlayerRecord> targets, Func<PlayerRecord, bool> op, Action? onAfter)
    {
        if (backend.State.Status != ConnectionStatus.Connected) { status.Text = Loc.S("Load game first"); return; }
        if (!backend.Session.Process.CanWrite) { status.Text = Loc.S("Editor is attached read-only"); return; }
        string label = Loc.S(actionKey);
        var report = RunBulk(backend.Edits, targets, label, op, RegisterPlayerLabels);
        FinishBulkUi(label, targets, report, reloadGameData: report.Changed > 0, onAfter);
    }

    void FinishBulkUi(string actionLabel, IReadOnlyList<PlayerRecord> targets, BulkActionReport report, bool reloadGameData, Action? onAfter)
    {
        status.Text = report.FormatStatus(actionLabel);
        if (reloadGameData)
        {
            var session = backend.Session;
            foreach (var t in targets)
            {
                try
                {
                    if (session.Players.Reload(t.Address) is { } fresh)
                    {
                        fresh.GameDate = backend.GameDate;
                        ReplaceRow(fresh);
                        var openTab = playerTabs.FirstOrDefault(tab => tab.Player?.Address == fresh.Address);
                        if (openTab is not null)
                        {
                            openTab.Player = fresh;
                            if (drafts.TryGetValue(fresh.Address, out var draft))
                                drafts[fresh.Address] = draft.Rebase(fresh);
                        }
                    }
                }
                catch { }
            }
            if (selected is not null && playerTabs.Any(tab => ReferenceEquals(tab.Root, bodyHost.Content) && tab.Player?.Address == selected.Address))
                RenderEditor();
            onAfter?.Invoke();
        }
        else if (report.Changed > 0)
        {
            onAfter?.Invoke();
        }
    }

    async Task ApplyBulkDevelopmentSpeedAsync(IReadOnlyList<PlayerRecord> targets, double multiplier, Action? onAfter)
    {
        if (backend.State.Status != ConnectionStatus.Connected || !backend.Session.Process.CanWrite) return;
        var accel = backend.Session.AcceleratedDevelopment;
        if (multiplier > 0) await Task.Run(() => accel.Install());
        string title = $"{Loc.S("Development Speed")} {(multiplier <= 0 ? Loc.S("Normal (1x)") : $"{multiplier.ToString("0.#", CultureInfo.InvariantCulture)}x")}";
        var report = RunBulk(backend.Edits, targets, title, p =>
        {
            accel.Set(p, multiplier);
            return true;
        }, RegisterPlayerLabels);
        FinishBulkUi(title, targets, report, reloadGameData: false, onAfter);
    }

    async Task PromptBulkDevelopmentSpeedAsync(IReadOnlyList<PlayerRecord> targets, Action? onAfter)
    {
        var input = new TextBox { Watermark = "2.5", Height = 28 };
        var ok = new Button { Content = Loc.S("Apply"), Classes = { "accent" }, IsDefault = true };
        var cancel = new Button { Content = Loc.S("Cancel") };
        var error = new TextBlock { Foreground = UiKit.Brush("#E05A64"), FontSize = 11.5, IsVisible = false };
        var win = new Window
        {
            Title = Loc.S("Set Development Speed"),
            Width = 360, Height = 170, CanResize = false,
            Background = UiKit.WindowBg, Foreground = UiKit.TextPrimary,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = Loc.S("Enter multiplier (1.1 – 20, or 1 for normal):"), FontSize = 12.5 },
                    input, error,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, ok } },
                },
            },
        };
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            if (ParseSpeed(input.Text) is not { } mult || mult != 0 && !AcceleratedDevelopment.IsAllowedMultiplier(mult))
            {
                error.Text = Loc.S("Enter multiplier (1.1 – 20, or 1 for normal):");
                error.IsVisible = true;
                return;
            }
            win.Close();
            _ = ApplyBulkDevelopmentSpeedAsync(targets, mult, onAfter);
        };
        await win.ShowDialog(this);
    }
}
