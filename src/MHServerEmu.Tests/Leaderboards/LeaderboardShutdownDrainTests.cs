using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using Gazillion;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Leaderboards;
using MHServerEmu.PlayerManagement.Players;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardShutdownDrainTests
    {
        [Fact]
        public async Task DisabledService_RemainsRunningUntilShutdown()
        {
            LeaderboardService service = new(JsonDBManager.Instance, new SQLiteLeaderboardDBManager("unused.db"));
            ServerManager manager = new();
            manager.RegisterGameService(service, GameServiceType.Leaderboard);

            Assert.True(manager.RunServices());
            Assert.Equal(GameServiceState.Running, service.State);
            Assert.False(manager.WaitForFaultAsync().IsCompleted);

            await Task.Run(manager.ShutdownServices).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public async Task Shutdown_AcceptedScorePersistsBeforeRuntimeDisposes()
        {
            RecordingStore store = new();
            LeaderboardDatabase database = CreateDatabase(store);
            LeaderboardService service = new(database, new LeaderboardRewardManager(store, new Publisher(), () => TimeSpan.Zero, () => { }), () => true, () => true);
            TaskCompletionSource<bool> servicesStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> consoleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool disposedAfterSave = false;
            PersistenceRuntime runtime = CreateRuntime(store, () => disposedAfterSave = store.SavedEntries.Any(entry => entry.ParticipantId == 9001 && entry.Score == 21));
            ServerStartupDependencies dependencies = new(
                (_, _) => Task.FromResult(runtime),
                () => true,
                (manager, _, _) => manager.RegisterGameService(service, GameServiceType.Leaderboard),
                () => consoleRead.Task,
                () => servicesStarted.SetResult(true));
            ServerApp app = new(dependencies, new ServerManager());

            Task run = app.RunAsync();
            await servicesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, store.SaveScoreBatchCount);
            ServiceMessage.LeaderboardScoreUpdateBatch scoreBatch = new(1);
            scoreBatch[0] = new(1, 9001, 0, 17, 3);
            service.ReceiveServiceMessage(scoreBatch);
            app.Shutdown();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, store.SaveScoreBatchCount);
            Assert.Contains(store.SavedEntries, entry => entry.ParticipantId == 9001 && entry.Score == 21 && entry.HighScore == 21);
            Assert.True(disposedAfterSave);
        }

        private static PersistenceRuntime CreateRuntime(RecordingStore store, Action dispose)
        {
            JsonDBManager manager = JsonDBManager.Instance;
            return new PersistenceRuntime(new PersistenceServices(manager, manager, manager, store, PersistenceCapabilities.Json), () =>
            {
                dispose();
                return ValueTask.CompletedTask;
            });
        }

        private static LeaderboardDatabase CreateDatabase(RecordingStore store)
        {
            LeaderboardDatabase database = new(store, new NameResolver(), new Catalog(), new Publisher(), new LeaderboardRuntimeOptions("unused.json", 1));
            LeaderboardPrototype prototype = (LeaderboardPrototype)RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardPrototype));
            ScoringEventAchievementScorePrototype scoringEvent = (ScoringEventAchievementScorePrototype)RuntimeHelpers.GetUninitializedObject(typeof(ScoringEventAchievementScorePrototype));
            LeaderboardScoringRuleIntPrototype rule = (LeaderboardScoringRuleIntPrototype)RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardScoringRuleIntPrototype));
            Leaderboard leaderboard = (Leaderboard)RuntimeHelpers.GetUninitializedObject(typeof(Leaderboard));
            LeaderboardInstance instance = (LeaderboardInstance)RuntimeHelpers.GetUninitializedObject(typeof(LeaderboardInstance));
            SetAutoProperty(scoringEvent, "Type", MHServerEmu.Games.Events.ScoringEventType.AchievementScore);
            SetAutoProperty(rule, "Event", scoringEvent);
            SetAutoProperty(rule, "GUID", 17L);
            SetAutoProperty(rule, "ValueInt", 7);
            SetAutoProperty(prototype, "DepthOfStandings", 10);
            SetAutoProperty(prototype, "RankingRule", LeaderboardRankingRule.Descending);
            SetAutoProperty(prototype, "ScoringRules", new LeaderboardScoringRulePrototype[] { rule });
            SetField(leaderboard, "_lock", new object());
            SetField(leaderboard, "_database", database);
            SetAutoProperty(leaderboard, "LeaderboardId", (PrototypeGuid)1);
            SetAutoProperty(leaderboard, "Prototype", prototype);
            SetAutoProperty(leaderboard, "ActiveInstance", instance);
            SetAutoProperty(leaderboard, "Instances", new List<LeaderboardInstance> { instance });
            SetField(instance, "_leaderboard", leaderboard);
            SetField(instance, "_lock", new object());
            SetField(instance, "_entryMap", new Dictionary<ulong, MHServerEmu.Leaderboards.LeaderboardEntry>());
            SetField(instance, "_percentileBuckets", new List<(LeaderboardPercentile Percentile, ulong Score)>());
            SetField(instance, "_nextAutoSaveTime", DateTime.MaxValue);
            SetAutoProperty(instance, "InstanceId", 1UL);
            SetAutoProperty(instance, "State", LeaderboardState.eLBS_Active);
            SetAutoProperty(instance, "Entries", new List<MHServerEmu.Leaderboards.LeaderboardEntry>());
            instance.ActivationTime = DateTime.UtcNow;
            instance.ExpirationTime = DateTime.UtcNow.AddDays(1);
            SetField(typeof(LeaderboardDatabase), database, "_leaderboards", new Dictionary<PrototypeGuid, Leaderboard> { [(PrototypeGuid)1] = leaderboard });
            return database;
        }

        private static void SetAutoProperty<T>(object target, string name, T value)
        {
            SetField(target, $"<{name}>k__BackingField", value);
        }

        private static void SetField(object target, string name, object value)
        {
            SetField(target.GetType(), target, name, value);
        }

        private static void SetField(Type type, object target, string name, object value)
        {
            for (; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    field.SetValue(target, value);
                    return;
                }
            }

            throw new InvalidOperationException($"Field {name} was not found on {target.GetType().Name}.");
        }

        private sealed class NameResolver : ILeaderboardPlayerNameResolver
        {
            public string GetPlayerName(ulong participantId) => participantId.ToString();
        }

        private sealed class Catalog : ILeaderboardPrototypeCatalog
        {
            public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes() => Array.Empty<LeaderboardPrototypeDefinition>();
            public bool TryGetPrototype(long leaderboardId, out LeaderboardPrototype prototype)
            {
                prototype = null;
                return false;
            }
        }

        private sealed class Publisher : ILeaderboardPublisher
        {
            public void Publish(ServiceMessage.LeaderboardStateChange change) { }
            public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes) { }
            public void Publish(ServiceMessage.LeaderboardRewardRequestResponse response) { }
        }

        private sealed class RecordingStore : ILeaderboardStore
        {
            public int SaveScoreBatchCount { get; private set; }
            public IReadOnlyList<LeaderboardEntryWrite> SavedEntries { get; private set; } = Array.Empty<LeaderboardEntryWrite>();

            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.NotFound; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) { SaveScoreBatchCount++; SavedEntries = request.Entries; return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { committedInstance = null; return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Success; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Success;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards) { rewards = Array.Empty<DBRewardEntry>(); return LeaderboardStoreResult.Success; }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Finalized;
        }
    }
}
