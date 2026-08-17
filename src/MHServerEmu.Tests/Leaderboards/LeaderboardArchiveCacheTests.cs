using MHServerEmu.Leaderboards;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardArchiveCacheTests
    {
        [Fact]
        public void RuntimeOptions_ClampsArchiveCacheCapacityToOne()
        {
            LeaderboardRuntimeOptions options = new("schedule.json", normalArchiveLimit: 0);

            Assert.Equal(1, options.ArchiveCacheCapacity);
        }

        [Fact]
        public void RuntimeOptions_UsesInjectedAutoSaveInterval()
        {
            LeaderboardRuntimeOptions options = new("schedule.json", normalArchiveLimit: 1, autoSaveIntervalMinutes: 7);

            Assert.Equal(7, options.AutoSaveIntervalMinutes);
        }

        [Fact]
        public void ArchiveCache_EvictsLeastRecentlyUsedEntryAtConfiguredCapacity()
        {
            LeaderboardArchiveCache<string> cache = new(capacity: 2);
            cache.Set(1, "one");
            cache.Set(2, "two");
            Assert.Equal("one", cache.Get(1));
            cache.Set(3, "three");

            Assert.False(cache.TryGet(2, out _));
            Assert.Equal("one", cache.Get(1));
            Assert.Equal("three", cache.Get(3));
        }

        [Fact]
        public void RuntimeArchiveLookup_CachesBoundedPageOnMiss()
        {
            ArchiveStore store = new([new DBLeaderboardInstance { LeaderboardId = 1, InstanceId = 10 }]);
            LeaderboardDatabase database = new(store, new NameResolver(), new Catalog(), new Publisher(),
                new LeaderboardRuntimeOptions("schedule.json", normalArchiveLimit: 2));

            Assert.True(TryLoadArchivedInstance(database, 1, 10, out DBLeaderboardInstance first));
            Assert.Equal(10, first.InstanceId);
            Assert.True(TryLoadArchivedInstance(database, 1, 10, out DBLeaderboardInstance second));
            Assert.Equal(10, second.InstanceId);
            Assert.Equal(new[] { (1L, 0L, 2) }, store.VisiblePageRequests);
        }

        private static bool TryLoadArchivedInstance(LeaderboardDatabase database, long leaderboardId, long instanceId, out DBLeaderboardInstance instance)
        {
            object[] arguments = [leaderboardId, instanceId, null];
            bool found = (bool)typeof(LeaderboardDatabase).GetMethod("TryLoadArchivedInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(database, arguments);
            instance = (DBLeaderboardInstance)arguments[2];
            return found;
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => participantId.ToString();
        }

        private sealed class Catalog : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() => Array.Empty<LeaderboardPrototypeDefinition>();
            public bool TryGetPrototype(long leaderboardId, out MHServerEmu.Games.GameData.Prototypes.LeaderboardPrototype prototype) { prototype = null; return false; }
        }

        private sealed class Publisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }

        private sealed class ArchiveStore(IReadOnlyList<DBLeaderboardInstance> page) : ILeaderboardStore
        {
            public List<(long LeaderboardId, long BeforeInstanceId, int Limit)> VisiblePageRequests { get; } = new();

            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances)
            {
                VisiblePageRequests.Add((leaderboardId, beforeInstanceId, limit));
                instances = page;
                return LeaderboardStoreResult.Success;
            }
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
