using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRewardManagerTests
    {
        [Fact]
        public void Update_PublishesPendingRewardsThroughInjectedPublisher()
        {
            RewardStore store = new();
            RecordingPublisher publisher = new();
            LeaderboardRewardManager manager = new(store, publisher);

            manager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            manager.Update();

            ServiceMessage.LeaderboardRewardRequestResponse response = Assert.Single(publisher.RewardResponses);
            Assert.Equal(42UL, response.ParticipantId);
            Assert.Equal(3UL, Assert.Single(response.Entries).RewardId);
        }

        private sealed class RecordingPublisher : ILeaderboardPublisher
        {
            public List<ServiceMessage.LeaderboardRewardRequestResponse> RewardResponses { get; } = new();

            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) => RewardResponses.Add(response);
        }

        private sealed class RewardStore : ILeaderboardStore
        {
            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { committedInstance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards)
            {
                rewards = [new DBRewardEntry(1, 2, 3, participantId, 1)];
                return LeaderboardStoreResult.Success;
            }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Failed;
        }
    }
}
