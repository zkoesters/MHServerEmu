using Gazillion;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRestartTests
    {
        [Fact]
        public void Initialize_MissingSchedule_GeneratesBeforeStoreContact()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                RecordingStore store = new(path);
                LeaderboardDatabase database = CreateDatabase(store, path);

                Assert.True(database.Initialize());
                Assert.Equal(new[] { "Initialize", "ReconcileSchedule" }, store.Calls);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void SecondDatabase_HasNoStateFromFirstInstance()
        {
            LeaderboardDatabase first = CreateDatabase(new RecordingStore(null), "first.json");
            LeaderboardDatabase second = CreateDatabase(new RecordingStore(null), "second.json");

            Assert.NotSame(first.GetLeaderboards(), second.GetLeaderboards());
        }

        [Fact]
        public void GetMetaEntries_LoadsPersistedMappingsFromStore()
        {
            RecordingStore store = new(null)
            {
                MetaMappings = [new LeaderboardMetaMapping(1, 10, 2, 20)],
            };
            LeaderboardDatabase database = CreateDatabase(store, "mappings.json");

            IReadOnlyList<DBMetaEntry> mappings = Assert.IsAssignableFrom<IReadOnlyList<DBMetaEntry>>(typeof(LeaderboardDatabase)
                .GetMethod("GetMetaEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(database, [1L, 10L]));

            DBMetaEntry mapping = Assert.Single(mappings);
            Assert.Equal(2, mapping.SubLeaderboardId);
            Assert.Equal(20, mapping.SubInstanceId);
            Assert.Contains("LoadMetaMappings", store.Calls);
        }

        private static LeaderboardDatabase CreateDatabase(ILeaderboardStore store, string schedulePath)
        {
            return new LeaderboardDatabase(store, new NameResolver(), new TestCatalog(
                new LeaderboardPrototypeDefinition(101, "LeaderboardOne", true, Array.Empty<long>())), new Publisher(),
                new LeaderboardRuntimeOptions(schedulePath, normalArchiveLimit: 1));
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => $"Player{participantId}";
        }

        private sealed class Publisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
        }

        private sealed class TestCatalog(params LeaderboardPrototypeDefinition[] definitions) : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() => definitions;
            public bool TryGetPrototype(long leaderboardId, out MHServerEmu.Games.GameData.Prototypes.LeaderboardPrototype prototype) { prototype = null; return false; }
        }

        private sealed class RecordingStore(string schedulePath) : ILeaderboardStore
        {
            public List<string> Calls { get; } = new();
            public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; init; } = Array.Empty<LeaderboardMetaMapping>();

            public LeaderboardStoreResult Initialize()
            {
                Calls.Add("Initialize");
                return File.Exists(schedulePath) ? LeaderboardStoreResult.Success : LeaderboardStoreResult.Failed;
            }

            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot)
            {
                Calls.Add("ReconcileSchedule");
                snapshot = new();
                return LeaderboardStoreResult.Success;
            }

            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { Calls.Add("LoadMetaMappings"); mappings = MetaMappings; return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { committedInstance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards) { rewards = Array.Empty<DBRewardEntry>(); return LeaderboardStoreResult.Failed; }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Failed;
        }
    }
}
