using Avalonia.Controls;
using Fm27Editor.Core.Players;

namespace Fm27Editor.App;

internal sealed partial class MassEditWindow
{
    /// <summary>Builds what each scope would change and opens the review tables; nothing is written until an Apply there.</summary>
    async Task ReviewAsync()
    {
        if (backend.State.Status != ConnectionStatus.Connected) { status.Text = Loc.S("Not connected"); return; }
        var session = backend.Session;
        var league = chosenLeague;
        if (Content is not Control root || !root.IsEnabled || choosingTargets) return;
        root.IsEnabled = false;
        string? outcome = null;
        try
        {
            // The form's values are copied here, on the window thread; building every plan (one read per
            // target per field) then runs in the background so the window stays responsive.
            SnapshotPlanInputs();
            List<MassPlan> plans;
            try
            {
                plans = await Task.Run(() =>
                {
                    var result = new List<MassPlan>();
                    var active = planFields.Where(f => f.ModeIndex != ModeNone).ToArray();
                    if (active.Any(f => f.Kind is >= Kind.PlayerAttribute and <= Kind.PlayerDetail) ||
                        planInjuryClear || planInjuryType is not null || planPresetAll || planPresetTu || planMatchCaToPa ||
                        planMoveStates.Any(s => s != 0) || planContractType > 0 || planContractStatus > 0 || planClauseType > 0 || planRemoveClause)
                        result.Add(PlayerPlan());
                    if (active.Any(f => f.Kind is >= Kind.StaffField and <= Kind.StaffDetail) || planMatchCaToPa) result.Add(CoachPlan());
                    if (active.Any(f => f.Kind is Kind.ClubField or Kind.StadiumField) || planClearDebts)
                    {
                        if (league is not null) CheckLeague(session, league);
                        result.Add(ClubPlan());
                    }
                    if (active.Any(f => f.Kind == Kind.NationField)) result.Add(NationPlan());
                    return result.Where(p => p.Rows.Count > 0).ToList();
                });
            }
            finally { ClearPlanSnapshot(); }
            if (plans.Count == 0) { outcome = Loc.S("Nothing would change"); return; }
            await new MassEditReviewWindow(plans, (chosen, progress, ct) =>
                WriteAsync(chosen, progress, ct, session, league)).ShowDialog(this);
        }
        catch (Exception ex) { outcome = UserMessage.For(ex); }
        finally
        {
            root.IsEnabled = true;
            MarkDirty();
            if (outcome is not null) status.Text = outcome;
        }
    }

    /// <summary>Writes the ticked lines of the given tables as one history group, then reloads their screens.</summary>
    async Task<int> WriteAsync(IReadOnlyList<MassPlan> plans, IProgress<(int Done, int Total)>? progress, CancellationToken ct,
        Fm27Editor.Core.Game.GameSession session, LeagueBatch? league)
    {
        int written = 0;
        int done = 0;
        int total = plans.Sum(p => p.Rows.Count(r => r.Include && r.CanApply));
        progress?.Report((0, total));
        string scopes = string.Join(", ", plans.Select(p => p.Scope));
        try
        {
            await Task.Run(() =>
            {
                if (!ReferenceEquals(session, backend.Session) || backend.SaveChanged || session.LoadState.SaveChanged)
                    throw new InvalidOperationException("The game data changed. Review the changes again.");
                if (league is not null && plans.Any(p => p.Scope == "Club")) CheckLeague(session, league);
                backend.Edits.Execute("Mass edit", scopes, $"Mass edit: {scopes}", () =>
                {
                    foreach (var plan in plans)
                        foreach (var row in plan.Rows.Where(r => r.Include && r.CanApply))
                        {
                            ct.ThrowIfCancellationRequested();
                            written += row.Apply!();
                            row.Include = false;
                            done++;
                            if (done % 200 == 0) progress?.Report((done, total));
                        }
                });
            }, ct);
        }
        catch (OperationCanceledException) { /* what was written stays in the history group; the caller reports it */ }
        finally
        {
            progress?.Report((done, total));
            foreach (var plan in plans) await host.MassEditReloadAsync(plan.Scope);
            if (written > 0 && backend.State.Status == ConnectionStatus.Connected) searchData = Task.Run(LoadSearchData);
        }
        return written;
    }
}
