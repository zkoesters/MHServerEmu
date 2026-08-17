using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRewardRetryTests
    {
        [Fact]
        public void FinalizationFailure_RetriesAtBoundedSchedule()
        {
            ManualClock clock = new();
            RewardStore store = new(RewardFinalizationResult.Failed);
            LeaderboardRewardManager manager = new(store, new RecordingPublisher(), () => clock.Now, () => { });

            manager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            manager.Update();
            manager.OnLeaderboardRewardConfirmation(new ServiceMessage.LeaderboardRewardConfirmation(1, 2, 42));
            manager.Update();

            Assert.Equal(1, store.FinalizationCalls);
            foreach ((int seconds, int expectedCalls) in new[] { (1, 2), (3, 3), (7, 4), (15, 5), (31, 6), (61, 7), (91, 8) })
            {
                clock.Now = TimeSpan.FromSeconds(seconds);
                manager.Update();
                Assert.Equal(expectedCalls, store.FinalizationCalls);
            }
        }

        [Fact]
        public void FinalizationOutcomeUncertain_RequestsShutdownAndStopsRetries()
        {
            ManualClock clock = new();
            RewardStore store = new(RewardFinalizationResult.OutcomeUncertain);
            int fatalCalls = 0;
            LeaderboardRewardManager manager = new(store, new RecordingPublisher(), () => clock.Now, () => fatalCalls++);

            manager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            manager.Update();
            manager.OnLeaderboardRewardConfirmation(new ServiceMessage.LeaderboardRewardConfirmation(1, 2, 42));
            manager.Update();
            clock.Now = TimeSpan.FromMinutes(10);
            manager.Update();

            Assert.Equal(1, fatalCalls);
            Assert.Equal(1, store.FinalizationCalls);
        }

        [Fact]
        public void FinalizationOutcomeUncertain_StopsOtherDueFinalizations()
        {
            ManualClock clock = new();
            RewardStore store = new(RewardFinalizationResult.Failed, RewardFinalizationResult.Failed, RewardFinalizationResult.OutcomeUncertain)
            {
                PendingRewardCount = 2,
            };
            int fatalCalls = 0;
            LeaderboardRewardManager manager = new(store, new RecordingPublisher(), () => clock.Now, () => fatalCalls++);

            manager.OnLeaderboardRewardRequest(new ServiceMessage.LeaderboardRewardRequest(42));
            manager.Update();
            manager.OnLeaderboardRewardConfirmation(new ServiceMessage.LeaderboardRewardConfirmation(1, 1, 42));
            manager.OnLeaderboardRewardConfirmation(new ServiceMessage.LeaderboardRewardConfirmation(2, 2, 42));
            manager.Update();
            clock.Now = TimeSpan.FromSeconds(1);
            manager.Update();

            Assert.Equal(1, fatalCalls);
            Assert.Equal(3, store.FinalizationCalls);
        }

        private sealed class ManualClock
        {
            public TimeSpan Now { get; set; }
        }

        private sealed class RecordingPublisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }

        private sealed class RewardStore(params RewardFinalizationResult[] finalizationResults) : ILeaderboardStore
        {
            private readonly Queue<RewardFinalizationResult> _finalizationResults = new(finalizationResults);

            public int FinalizationCalls { get; private set; }
            public int PendingRewardCount { get; set; } = 1;

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
                rewards = Enumerable.Range(1, PendingRewardCount)
                    .Select(index => new DBRewardEntry(index, index, 3, participantId, 1))
                    .ToArray();
                return LeaderboardStoreResult.Success;
            }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate)
            {
                FinalizationCalls++;
                return _finalizationResults.Count > 0 ? _finalizationResults.Dequeue() : RewardFinalizationResult.Failed;
            }
        }
    }
}
