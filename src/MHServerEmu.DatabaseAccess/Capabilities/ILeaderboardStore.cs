using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess
{
    public interface ILeaderboardStore
    {
        LeaderboardStoreResult Initialize();
        LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot);
        LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries);
        LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance);
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
