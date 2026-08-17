using Gazillion;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.GameData;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardMetaRotationTests
    {
        [Fact]
        public void GetNewMetaEntries_ChildRotated_UsesChildCurrentActiveInstance()
        {
            LeaderboardDatabase database = (LeaderboardDatabase)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardDatabase));
            typeof(LeaderboardDatabase).GetField("_leaderboardLock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(database, new object());
            Dictionary<PrototypeGuid, Leaderboard> leaderboards = new();
            typeof(LeaderboardDatabase).GetField("_leaderboards", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(database, leaderboards);
            typeof(LeaderboardDatabase).GetField("_metaLeaderboards", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(database, new Dictionary<PrototypeGuid, Leaderboard>());

            Leaderboard child = CreateLeaderboard(database, leaderboardId: 2);
            LeaderboardInstance childActive = CreateInstance(child, instanceId: 20);
            typeof(Leaderboard).GetField("<ActiveInstance>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(child, childActive);
            leaderboards[(PrototypeGuid)2] = child;

            Leaderboard parent = CreateLeaderboard(database, leaderboardId: 1);
            LeaderboardInstance previousMetaInstance = CreateInstance(parent, instanceId: 100);
            MetaLeaderboardEntry staleMapping = new((PrototypeGuid)2, null) { SubInstanceId = 10 };
            typeof(LeaderboardInstance).GetField("_metaLeaderboardEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(previousMetaInstance, new List<MetaLeaderboardEntry> { staleMapping });

            IReadOnlyList<DBMetaEntry> mappings = previousMetaInstance.GetNewMetaEntries(101);

            DBMetaEntry mapping = Assert.Single(mappings);
            Assert.Equal(20, mapping.SubInstanceId);
        }

        [Fact]
        public void AddNewInstance_ChildHasNoActiveInstance_DoesNotPersistRotation()
        {
            RecordingStore store = new();
            LeaderboardDatabase database = new(store, new NameResolver(), new EmptyCatalog(), new EmptyPublisher(),
                new LeaderboardRuntimeOptions("schedule.json", normalArchiveLimit: 1));
            Leaderboard parent = CreateLeaderboard(database, leaderboardId: 1);
            LeaderboardInstance previousMetaInstance = CreateInstance(parent, instanceId: 100);
            MetaLeaderboardEntry mapping = new((PrototypeGuid)2, null) { SubInstanceId = 10 };
            typeof(LeaderboardInstance).GetField("_metaLeaderboardEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(previousMetaInstance, new List<MetaLeaderboardEntry> { mapping });
            DBLeaderboardInstance nextInstance = new() { InstanceId = 101, LeaderboardId = 1, State = LeaderboardState.eLBS_Created };

            bool added = (bool)typeof(Leaderboard).GetMethod("AddNewInstance", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(parent, [nextInstance, previousMetaInstance]);

            Assert.False(added);
            Assert.Equal(0, store.RotationCount);
        }

        private static Leaderboard CreateLeaderboard(LeaderboardDatabase database, ulong leaderboardId)
        {
            Leaderboard leaderboard = (Leaderboard)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Leaderboard));
            typeof(Leaderboard).GetField("_database", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(leaderboard, database);
            typeof(Leaderboard).GetField("<LeaderboardId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(leaderboard, (PrototypeGuid)leaderboardId);
            return leaderboard;
        }

        private static LeaderboardInstance CreateInstance(Leaderboard leaderboard, ulong instanceId)
        {
            LeaderboardInstance instance = (LeaderboardInstance)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardInstance));
            typeof(LeaderboardInstance).GetField("_leaderboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, leaderboard);
            typeof(LeaderboardInstance).GetField("<InstanceId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, instanceId);
            return instance;
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => participantId.ToString();
        }

        private sealed class EmptyCatalog : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() => Array.Empty<LeaderboardPrototypeDefinition>();

            public bool TryGetPrototype(long leaderboardId, out MHServerEmu.Games.GameData.Prototypes.LeaderboardPrototype prototype)
            {
                prototype = null;
                return false;
            }
        }

        private sealed class EmptyPublisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }

        private sealed class RecordingStore : ILeaderboardStore
        {
            public int RotationCount { get; private set; }

            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { RotationCount++; committedInstance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards) { rewards = Array.Empty<DBRewardEntry>(); return LeaderboardStoreResult.Success; }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Finalized;
        }
    }
}
