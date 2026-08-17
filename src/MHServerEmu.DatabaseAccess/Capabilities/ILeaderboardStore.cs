using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess
{
    /// <summary>
    /// Provides atomic, provider-neutral leaderboard persistence operations.
    /// </summary>
    /// <remarks>
    /// Providers return detached deep copies for every <c>DB*</c> output row and collection element, including cloned
    /// <see cref="DBLeaderboardEntry.RuleStates"/> bytes; they never return tracked or cache-owned entities. On any
    /// non-success result, providers assign empty output collections and snapshots and default output rows.
    /// </remarks>
    [LeaderboardStoreOutputContract]
    public interface ILeaderboardStore
    {
        LeaderboardStoreResult Initialize();
        LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot);
        LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries);
        LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance);
        /// <summary>
        /// Loads one bounded page of visible instances in descending instance-ID order.
        /// </summary>
        /// <remarks>
        /// Before opening a connection, every provider must call <see cref="LeaderboardStoreValidator.ValidateVisibleInstancesLimit"/>.
        /// Limits outside 1 through 100 return <see cref="LeaderboardStoreResult.InvalidData"/> and an empty <paramref name="instances"/>
        /// output. A cursor of zero starts at the newest instance; later cursors are exclusive.
        /// </remarks>
        [LeaderboardPaginationContract]
        LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances);
        LeaderboardStoreResult ActivateInstance(LeaderboardActivation request);
        LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request);
        LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request);
        LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance);
        LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot);
        LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request);
        LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards);
        RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate);
    }
}
