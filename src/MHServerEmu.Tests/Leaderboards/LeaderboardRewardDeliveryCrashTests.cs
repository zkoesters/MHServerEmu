using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRewardDeliveryCrashTests
    {
        [Fact]
        public void GrantBeforeConfirmation_RestartRedeliversPendingReward()
        {
            DurableRewardStore store = new(new DBRewardEntry(1, 2, 3, 42, 1));
            RecordingPublisher firstPublisher = new();
            LeaderboardRewardManager firstManager = new(store, firstPublisher, () => TimeSpan.Zero, () => { });

            firstManager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            firstManager.Update();

            Assert.Single(firstPublisher.RewardResponses);
            Assert.True(store.HasPendingReward);

            RecordingPublisher restartedPublisher = new();
            LeaderboardRewardManager restartedManager = new(store, restartedPublisher, () => TimeSpan.Zero, () => { });
            restartedManager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            restartedManager.Update();

            Assert.Single(restartedPublisher.RewardResponses);
            Assert.True(store.HasPendingReward);
        }

        [Fact]
        public void ConfirmationBeforeResponse_RestartDoesNotRedeliverDurablyFinalizedReward()
        {
            DurableRewardStore store = new(new DBRewardEntry(1, 2, 3, 42, 1));
            LeaderboardRewardManager firstManager = new(store, new RecordingPublisher(), () => TimeSpan.FromSeconds(100), () => { });

            firstManager.OnLeaderboardRewardConfirmation(new ServiceMessage.LeaderboardRewardConfirmation(1, 2, 42));
            firstManager.Update();

            Assert.False(store.HasPendingReward);

            RecordingPublisher restartedPublisher = new();
            LeaderboardRewardManager restartedManager = new(store, restartedPublisher, () => TimeSpan.FromSeconds(200), () => { });
            restartedManager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            restartedManager.Update();

            Assert.Empty(restartedPublisher.RewardResponses);
        }

        private sealed class RecordingPublisher : ILeaderboardPublisher
        {
            public List<ServiceMessage.LeaderboardRewardRequestResponse> RewardResponses { get; } = new();

            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) => RewardResponses.Add(response);
        }

        private sealed class DurableRewardStore(DBRewardEntry reward) : ILeaderboardStore
        {
            private readonly DBRewardEntry _reward = reward;

            public bool HasPendingReward => _reward.RewardedDate == 0;

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
                rewards = _reward.ParticipantId == participantId && HasPendingReward
                    ? [new DBRewardEntry(_reward.LeaderboardId, _reward.InstanceId, _reward.RewardId, _reward.ParticipantId, _reward.Rank)]
                    : Array.Empty<DBRewardEntry>();
                return LeaderboardStoreResult.Success;
            }

            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate)
            {
                if (key != new LeaderboardRewardKey(_reward.LeaderboardId, _reward.InstanceId, _reward.ParticipantId))
                    return RewardFinalizationResult.NotFound;

                if (HasPendingReward == false)
                    return RewardFinalizationResult.AlreadyFinalized;

                _reward.RewardedDate = rewardedDate;
                return RewardFinalizationResult.Finalized;
            }
        }
    }
}
