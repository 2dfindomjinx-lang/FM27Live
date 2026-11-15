using Fm27Editor.App;
using Fm27Editor.Core.Game;
using System.Runtime.CompilerServices;

namespace Fm27Editor.App.Tests;

public sealed class TransferDealLoadingTests
{
    static GameSession Session() => (GameSession)RuntimeHelpers.GetUninitializedObject(typeof(GameSession));

    [Fact]
    public void Empty_index_is_loaded_once_and_tab_reads_never_scan()
    {
        var session = Session();
        var data = new LoadedGameData(session);
        var report = new GameLoadReport();
        int scans = 0;
        data.LoadTransferDeals(report, () => { scans++; return []; });
        data.LoadTransferDeals(report, () => throw new Exception("Unexpected rescan"));
        Assert.Empty(data.TransferDealCandidates(session));
        Assert.Empty(data.TransferDealCandidates(session));
        Assert.Equal(1, scans);
        Assert.True(data.DealIndexLoaded);
        Assert.True(report.Succeeded);
    }

    [Fact]
    public void Failed_index_keeps_load_incomplete_and_never_retries_on_access()
    {
        var session = Session();
        var data = new LoadedGameData(session);
        var report = new GameLoadReport();
        var failure = new IOException("unreadable fixture");
        data.LoadTransferDeals(report, () => throw failure);
        data.LoadTransferDeals(report, () => throw new Exception("Unexpected rescan"));
        Assert.True(data.DealIndexAttempted);
        Assert.False(data.DealIndexLoaded);
        Assert.Same(failure, data.DealIndexError);
        Assert.False(report.Succeeded);
        Assert.Contains("transfer deals", Assert.Single(report.Errors));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => data.TransferDealCandidates(session)).InnerException);
    }

    [Fact]
    public void Reconnection_rejects_old_candidates_and_reads_fresh_candidates()
    {
        var oldSession = Session();
        var nextSession = Session();
        var oldData = new LoadedGameData(oldSession);
        oldData.LoadTransferDeals(new(), () => [0x10000UL]);
        Assert.Throws<InvalidOperationException>(() => oldData.TransferDealCandidates(nextSession));
        var nextData = new LoadedGameData(nextSession);
        nextData.LoadTransferDeals(new(), () => [0x20000UL]);
        Assert.Equal(new ulong[] { 0x20000 }, nextData.TransferDealCandidates(nextSession));
    }

    [Fact]
    public void Uninitialized_index_cannot_be_reported_as_empty()
    {
        var session = Session();
        var data = new LoadedGameData(session);
        Assert.Throws<InvalidOperationException>(() => data.TransferDealCandidates(session));
        Assert.False(data.DealIndexAttempted);
    }
}
