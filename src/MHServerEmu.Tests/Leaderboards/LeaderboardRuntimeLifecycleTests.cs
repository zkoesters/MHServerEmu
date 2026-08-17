using Gazillion;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRuntimeLifecycleTests
    {
        [Fact]
        public void LoadOrCreate_ExistingLeaderboardId_ResolvesPrototypeIdentityThroughCatalog()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, """
                    [{ "LeaderboardId": 101, "IsEnabled": true, "StartTime": "2026-01-01T00:00:00Z", "MaxResetCount": 0 }]
                    """);
                LeaderboardScheduleLoader loader = new(new Catalog(), DateTime.UtcNow);

                Assert.True(loader.TryLoadOrCreate(path, normalArchiveLimit: 1, out LeaderboardReconciliation reconciliation));
                LeaderboardDefinitionSpec definition = Assert.Single(reconciliation.DesiredDefinitions);
                Assert.Equal(101, definition.LeaderboardId);
                Assert.Equal("LeaderboardOne", definition.PrototypeName);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Initialize_PublishesInitialCommittedSnapshot()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboard-schedule-{Guid.NewGuid():N}.json");
            try
            {
                RecordingStore store = new();
                RecordingPublisher publisher = new();
                LeaderboardDatabase database = new(store, new NameResolver(), new EmptyCatalog(), publisher,
                    new LeaderboardRuntimeOptions(path, normalArchiveLimit: 1));

                Assert.True(database.Initialize());
                Assert.Single(publisher.InitialStates);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LifecycleOperations_UseInjectedStoreAndPublishNothingBeforeRuntimeCommit()
        {
            RecordingStore store = new();
            RecordingPublisher publisher = new();
            LeaderboardDatabase database = new(store, new NameResolver(), new EmptyCatalog(), publisher,
                new LeaderboardRuntimeOptions("schedule.json", normalArchiveLimit: 1));
            DBLeaderboardInstance next = new()
            {
                InstanceId = 2,
                LeaderboardId = 1,
                State = LeaderboardState.eLBS_Created,
                Visible = true,
            };

            Assert.Equal(LeaderboardStoreResult.Success, Persist(database, "PersistActivation", new LeaderboardActivation(1, 1, 1)));
            Assert.Equal(LeaderboardStoreResult.Success, Persist(database, "PersistExpiration", new LeaderboardExpiration(1, 1, 1, LeaderboardState.eLBS_Active, Array.Empty<DBLeaderboardEntry>())));
            Assert.Equal(LeaderboardStoreResult.Success, PersistRotation(database, new(1, 1, LeaderboardState.eLBS_Expired,
                LeaderboardState.eLBS_Expired, next, LeaderboardState.eLBS_Created, Array.Empty<DBMetaEntry>())));
            Assert.Equal(LeaderboardStoreResult.Success, Persist(database, "PersistRewards", new LeaderboardRewardGeneration(1, 1, 1, LeaderboardState.eLBS_Expired, Array.Empty<DBRewardEntry>())));
            Assert.Equal(LeaderboardStoreResult.Success, PersistVisibility(database, new LeaderboardVisibilityRequest(1, 1, 100)));
            Assert.Empty(publisher.StateChanges);
        }

        [Fact]
        public void SaveEntries_FailedStoreWrite_RetainsDirtyEntries()
        {
            RecordingStore store = new() { ScoreResult = LeaderboardStoreResult.Failed };
            LeaderboardInstance instance = CreateInstance(store, LeaderboardState.eLBS_Active, 1);
            MHServerEmu.Leaderboards.LeaderboardEntry entry = new(new DBLeaderboardEntry { ParticipantId = 7, Score = 3, HighScore = 3, RuleStates = new byte[sizeof(int)] })
            {
                SaveRequired = true,
            };
            instance.Entries.Add(entry);

            Assert.False(instance.SaveEntries());
            Assert.True(entry.SaveRequired);
        }

        [Fact]
        public void ExpireInstance_FailedStoreWrite_LeavesRuntimeStateUnpublished()
        {
            RecordingStore store = new() { ExpirationResult = LeaderboardStoreResult.Failed };
            LeaderboardInstance instance = CreateInstance(store, LeaderboardState.eLBS_Active, 1);

            Assert.False(instance.SetState(LeaderboardState.eLBS_Expired));
            Assert.Equal(LeaderboardState.eLBS_Active, instance.State);
        }

        private static LeaderboardInstance CreateInstance(RecordingStore store, LeaderboardState state, ulong instanceId)
        {
            LeaderboardDatabase database = new(store, new NameResolver(), new EmptyCatalog(), new RecordingPublisher(),
                new LeaderboardRuntimeOptions("schedule.json", normalArchiveLimit: 1));
            Leaderboard leaderboard = (Leaderboard)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Leaderboard));
            typeof(Leaderboard).GetField("_database", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(leaderboard, database);
            typeof(Leaderboard).GetField("<LeaderboardId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(leaderboard, (MHServerEmu.Games.GameData.PrototypeGuid)1);
            LeaderboardInstance instance = (LeaderboardInstance)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardInstance));
            typeof(LeaderboardInstance).GetField("_leaderboard", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, leaderboard);
            typeof(LeaderboardInstance).GetField("_lock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, new object());
            typeof(LeaderboardInstance).GetField("<InstanceId>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, instanceId);
            typeof(LeaderboardInstance).GetField("<State>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, state);
            typeof(LeaderboardInstance).GetField("<Entries>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(instance, new List<MHServerEmu.Leaderboards.LeaderboardEntry>());
            return instance;
        }

        private static LeaderboardStoreResult Persist(LeaderboardDatabase database, string methodName, object request)
        {
            return (LeaderboardStoreResult)typeof(LeaderboardDatabase).GetMethod(methodName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(database, [request]);
        }

        private static LeaderboardStoreResult PersistRotation(LeaderboardDatabase database, LeaderboardRotation request)
        {
            object[] arguments = [request, null];
            return (LeaderboardStoreResult)typeof(LeaderboardDatabase).GetMethod("PersistRotation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(database, arguments);
        }

        private static LeaderboardStoreResult PersistVisibility(LeaderboardDatabase database, LeaderboardVisibilityRequest request)
        {
            object[] arguments = [request, null];
            return (LeaderboardStoreResult)typeof(LeaderboardDatabase).GetMethod("PersistVisibility",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(database, arguments);
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => participantId.ToString();
        }

        private sealed class Catalog : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() =>
                [new LeaderboardPrototypeDefinition(101, "LeaderboardOne", true, Array.Empty<long>())];

            public bool TryGetPrototype(long leaderboardId, out MHServerEmu.Games.GameData.Prototypes.LeaderboardPrototype prototype)
            {
                prototype = null;
                return false;
            }
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

        private sealed class RecordingPublisher : ILeaderboardPublisher
        {
            public List<IReadOnlyList<ServiceMessage.LeaderboardStateChange>> InitialStates { get; } = new();
            public List<ServiceMessage.LeaderboardStateChange> StateChanges { get; } = new();

            public void Publish(ServiceMessage.LeaderboardStateChange change) => StateChanges.Add(change);
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) => InitialStates.Add(changes);
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }

        private sealed class RecordingStore : ILeaderboardStore
        {
            public LeaderboardStoreResult ScoreResult { get; init; } = LeaderboardStoreResult.Success;
            public LeaderboardStoreResult ExpirationResult { get; init; } = LeaderboardStoreResult.Success;

            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) => ScoreResult;
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => ExpirationResult;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { committedInstance = new DBLeaderboardInstance { InstanceId = 2, LeaderboardId = 1, State = LeaderboardState.eLBS_Created }; return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards) { rewards = Array.Empty<DBRewardEntry>(); return LeaderboardStoreResult.Success; }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Finalized;
        }
    }
}
