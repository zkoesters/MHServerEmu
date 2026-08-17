using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Migrations;
using System.Reflection;
using Npgsql;
using NpgsqlTypes;
using Gazillion;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL.Stores
{
    [Trait("Category", "PostgreSQLIntegration")]
    [Collection("PostgreSQL migration integration")]
    public class PostgreSQLLeaderboardStoreTests
    {
        private const long ReconciliationLockKey = unchecked((long)0x4C6561646572626FUL);
        private readonly PostgreSQLTestDatabase _database;

        public PostgreSQLLeaderboardStoreTests(PostgreSQLTestDatabase database)
        {
            _database = database;
        }

        [Fact]
        public void StoreType_IsAvailableForLeaderboardPersistence()
        {
            Assert.NotNull(typeof(PostgreSQLProvider).Assembly.GetType("MHServerEmu.DatabaseAccess.PostgreSQL.PostgreSQLLeaderboardStore"));
        }

        [PostgreSQLIntegrationFact]
        public async Task Initialize_LoadsMigratedLeaderboardSchema()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.Initialize());
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.Initialize());
        }

        [PostgreSQLIntegrationFact]
        public async Task Loads_ReturnDetachedRowsAndClassifyMissingData()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true);
            await InsertEntryAsync(fixture, 10, 20, [1, 2, 3]);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadEntries(10, out IReadOnlyList<DBLeaderboardEntry> entries));
            DBLeaderboardEntry entry = Assert.Single(entries);
            entry.RuleStates[0] = 99;
            Assert.Equal(new byte[] { 1, 2, 3 }, await ReadEntryRuleStatesAsync(fixture, 10, 20));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadInstance(1, 10, out DBLeaderboardInstance instance));
            instance.Visible = false;
            Assert.True(await ReadInstanceVisibleAsync(fixture, 10));

            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Leaderboards.LoadEntries(99, out IReadOnlyList<DBLeaderboardEntry> missingEntries));
            Assert.Empty(missingEntries);
            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Leaderboards.LoadInstance(1, 99, out DBLeaderboardInstance missingInstance));
            Assert.Null(missingInstance);
        }

        [PostgreSQLIntegrationFact]
        public async Task LoadVisibleInstances_ValidatesBoundsAndPaginatesUnsignedIds()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.LoadVisibleInstances(1, 0, 0, out IReadOnlyList<DBLeaderboardInstance> invalid));
            Assert.Empty(invalid);

            long[] expected = { -1, -2, long.MinValue, 7, 1 };
            await InsertDefinitionAsync(fixture, 1);
            foreach (long instanceId in expected)
                await InsertInstanceAsync(fixture, instanceId, 1, true);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, 0, 3, out IReadOnlyList<DBLeaderboardInstance> first));
            Assert.Equal(expected.Take(3), first.Select(instance => instance.InstanceId));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, first[^1].InstanceId, 3, out IReadOnlyList<DBLeaderboardInstance> second));
            Assert.Equal(expected.Skip(3), second.Select(instance => instance.InstanceId));
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_CreatesReplaysAndUpdatesCommittedSnapshot()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            LeaderboardReconciliation request = Reconciliation(1, activationDate: 300);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out LeaderboardSnapshot first));
            Assert.Equal(1, Assert.Single(first.NonterminalInstances).InstanceId);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out LeaderboardSnapshot replay));
            Assert.Equal(first.Definitions, replay.Definitions);
            Assert.Equal(first.NonterminalInstances, replay.NonterminalInstances);
            Assert.Equal(first.NormalArchiveInstances, replay.NormalArchiveInstances);
            Assert.Equal(first.MetaMappings, replay.MetaMappings);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, startTime: 900, maxResetCount: 4, activationDate: 700), out LeaderboardSnapshot updated));
            Assert.Equal(900, Assert.Single(updated.Definitions).StartTime);
            Assert.Equal(300, Assert.Single(updated.NonterminalInstances).ActivationDate);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_DisablesReenablesAndRepairsZeroActivationDate()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, activationDate: 300), out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, enabled: false), out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadInstance(1, 1, out DBLeaderboardInstance disabled));
            Assert.Equal(LeaderboardState.eLBS_Rewarded, disabled.State);
            Assert.False(disabled.Visible);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, activationDate: 0, currentTime: 500), out LeaderboardSnapshot reenabled));
            LeaderboardInstanceSpec active = Assert.Single(reenabled.NonterminalInstances);
            Assert.Equal(2, active.InstanceId);
            Assert.Equal(500, active.ActivationDate);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_RejectsTopologyCollisionAndOverflowWithoutWrites()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            LeaderboardReconciliation collision = new(
                [new LeaderboardDefinitionSpec(1, "One", true, 100, 0), new LeaderboardDefinitionSpec(2, "Two", true, 100, 0)],
                [new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true), new LeaderboardInstanceSpec(0, 2, LeaderboardState.eLBS_Created, 300, true)],
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.ReconcileSchedule(collision, out _));

            long highBitId = unchecked((long)0xABCDEF1200000042UL);
            long maxInstanceId = unchecked((long)0xABCDEF12FFFFFFFFUL);
            await InsertDefinitionAsync(fixture, highBitId, false);
            await InsertInstanceAsync(fixture, maxInstanceId, highBitId, false, LeaderboardState.eLBS_Rewarded);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.ReconcileSchedule(Reconciliation(highBitId), out _));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, startTime: 100), out _));
            LeaderboardReconciliation invalidTopology = new(
                [new LeaderboardDefinitionSpec(1, "Leaderboard1", true, 900, 0)],
                [new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true)],
                [new LeaderboardMetaMapping(1, 1, 999, 1)], 500, 2);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.ReconcileSchedule(invalidTopology, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_RejectsNegativeMaxResetCountBeforeWriting()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1, maxResetCount: -1), out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM mhserveremu.leaderboard"));
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_OrdersHighBitDefinitionsAndGeneratesUnsignedInstanceIds()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            long highBitId = unchecked((long)0xABCDEF1200000042UL);
            LeaderboardReconciliation request = new(
                [new LeaderboardDefinitionSpec(highBitId, "High", true, 100, 0), new LeaderboardDefinitionSpec(1, "Low", true, 100, 0)],
                [new LeaderboardInstanceSpec(0, highBitId, LeaderboardState.eLBS_Created, 300, true), new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true)],
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out LeaderboardSnapshot snapshot));
            Assert.Equal(new[] { 1L, highBitId }, snapshot.Definitions.Select(definition => definition.LeaderboardId));
            Assert.Contains(snapshot.NonterminalInstances, instance => instance.InstanceId == unchecked((long)0xABCDEF1200000001UL));
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_SeparatesNonterminalInstancesAndBoundsArchives()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            LeaderboardReconciliation request = Reconciliation(1, normalArchiveLimit: 1);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out _));
            await InsertInstanceAsync(fixture, 2, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await InsertInstanceAsync(fixture, 3, 1, true, LeaderboardState.eLBS_Rewarded, false);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out LeaderboardSnapshot snapshot));
            Assert.Equal(new[] { 1L }, snapshot.NonterminalInstances.Select(instance => instance.InstanceId));
            Assert.Equal(new[] { 3L }, snapshot.NormalArchiveInstances.Select(instance => instance.InstanceId));
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_LoadsMappingsOnlyForSelectedSnapshotParents()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            long subLeaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long subInstanceId = unchecked((long)0xABCDEF1200000001UL);
            LeaderboardReconciliation request = new(
                [new LeaderboardDefinitionSpec(1, "Parent", true, 100, 0), new LeaderboardDefinitionSpec(subLeaderboardId, "Sub", true, 100, 0)],
                [new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true), new LeaderboardInstanceSpec(0, subLeaderboardId, LeaderboardState.eLBS_Created, 300, true)],
                [new LeaderboardMetaMapping(1, 1, subLeaderboardId, subInstanceId)], 500, 1);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out _));
            await InsertInstanceAsync(fixture, 2, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await InsertInstanceAsync(fixture, 3, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await InsertMappingAsync(fixture, 2, 1, subLeaderboardId, subInstanceId);
            await InsertMappingAsync(fixture, 3, 1, subLeaderboardId, subInstanceId);

            FieldInfo hookField = typeof(PostgreSQLLeaderboardStore).GetField("SnapshotMetaMappingCommandHook", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(hookField);
            string sql = null;
            try
            {
                hookField.SetValue(null, (Action<string>)(command => sql = command));
                Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(request, out LeaderboardSnapshot snapshot));
                Assert.Equal(new[] { 1L, 3L }, snapshot.MetaMappings.Select(mapping => mapping.InstanceId));
            }
            finally
            {
                hookField.SetValue(null, null);
            }

            Assert.Contains("instance_id = ANY(@instanceIds)", sql, StringComparison.Ordinal);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_ConcurrentUpdatesRemainSerializable()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ReconcileSchedule(Reconciliation(1), out _));

            LeaderboardStoreResult[] results = await ReconcileBehindGlobalLockAsync(fixture, Reconciliation(1, startTime: 200), Reconciliation(1, startTime: 300));

            Assert.All(results, result => Assert.Equal(LeaderboardStoreResult.Success, result));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadInstance(1, 1, out DBLeaderboardInstance active));
            Assert.Equal(LeaderboardState.eLBS_Created, active.State);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_ConcurrentFreshRequestsCreateOneInstance()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            LeaderboardReconciliation request = Reconciliation(1);

            LeaderboardStoreResult[] results = await ReconcileBehindGlobalLockAsync(fixture, request, request);

            Assert.All(results, result => Assert.Equal(LeaderboardStoreResult.Success, result));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, 0, 10, out IReadOnlyList<DBLeaderboardInstance> instances));
            Assert.Single(instances);
        }

        [PostgreSQLIntegrationFact]
        public async Task ReconcileSchedule_ConcurrentDisjointSchedulesSerializeGlobally()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            long highBitId = unchecked((long)0xABCDEF1200000002UL);

            LeaderboardStoreResult[] results = await ReconcileBehindGlobalLockAsync(fixture, Reconciliation(1), Reconciliation(highBitId));

            Assert.All(results, result => Assert.Equal(LeaderboardStoreResult.Success, result));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, 0, 10, out IReadOnlyList<DBLeaderboardInstance> first));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(highBitId, 0, 10, out IReadOnlyList<DBLeaderboardInstance> second));
            Assert.Equal(1, first.Count + second.Count);
        }

        [PostgreSQLIntegrationFact]
        public async Task ActivateInstance_TransitionsCreatedInstanceAndReplaysExactly()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Created);
            LeaderboardActivation request = new(1, 10, 10);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ActivateInstance(request));
            Assert.Equal((short)LeaderboardState.eLBS_Active, await ScalarAsync(fixture,
                "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 10"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ActivateInstance(request));

            await InsertDefinitionAsync(fixture, 2);
            await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Created);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.ActivateInstance(new(1, 10, 20)));
            Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Leaderboards.ActivateInstance(new(1, 11, 10)));
        }

        [PostgreSQLIntegrationFact]
        public async Task SaveScoreBatch_UpsertsOneThousandSignedEntriesAndPreservesRuleStates()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Active);
            long highBitParticipantId = unchecked((long)0xFEDCBA9876543210UL);
            List<LeaderboardEntryWrite> entries = Enumerable.Range(0, 1000)
                .Select(index => new LeaderboardEntryWrite(10, index == 999 ? highBitParticipantId : index + 1,
                    index == 999 ? long.MinValue : index, index == 999 ? long.MaxValue : index + 1,
                    [(byte)(index % 251), (byte)(index / 251)]))
                .ToList();

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active, entries)));
            Assert.Equal(1000L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM mhserveremu.leaderboard_entry WHERE instance_id = 10"));
            Assert.Equal(long.MinValue, await ScalarAsync(fixture,
                "SELECT score FROM mhserveremu.leaderboard_entry WHERE instance_id = 10 AND participant_id = @participantId",
                ("participantId", NpgsqlDbType.Bigint, highBitParticipantId)));
            Assert.Equal(long.MaxValue, await ScalarAsync(fixture,
                "SELECT high_score FROM mhserveremu.leaderboard_entry WHERE instance_id = 10 AND participant_id = @participantId",
                ("participantId", NpgsqlDbType.Bigint, highBitParticipantId)));
            Assert.Equal(new byte[] { 246, 3 }, await ReadEntryRuleStatesAsync(fixture, 10, highBitParticipantId));

            LeaderboardScoreBatch replacement = new(1, 10, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(10, highBitParticipantId, long.MaxValue, long.MinValue, [9, 8, 7])]);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.SaveScoreBatch(replacement));
            Assert.Equal(1000L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM mhserveremu.leaderboard_entry WHERE instance_id = 10"));
            Assert.Equal(long.MaxValue, await ScalarAsync(fixture,
                "SELECT score FROM mhserveremu.leaderboard_entry WHERE instance_id = 10 AND participant_id = @participantId",
                ("participantId", NpgsqlDbType.Bigint, highBitParticipantId)));
            Assert.Equal(new byte[] { 9, 8, 7 }, await ReadEntryRuleStatesAsync(fixture, 10, highBitParticipantId));

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(10, 21, 1, 1, [1]), new LeaderboardEntryWrite(10, 21, 2, 2, [2])])));
        }

        [PostgreSQLIntegrationFact]
        public async Task ExpireInstance_PersistsExactFinalRowsAndReplaysAfterRotation()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Active);
            LeaderboardExpiration request = new(1, 10, 10, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(10, 20, long.MinValue, long.MaxValue, [1, 2, 3])]);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ExpireInstance(request));
            Assert.Equal((short)LeaderboardState.eLBS_Expired, await ScalarAsync(fixture,
                "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 10"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ExpireInstance(request));
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Leaderboards.ExpireInstance(new(1, 10, 10, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(10, 20, long.MinValue, 1, [1, 2, 3])])));

            LeaderboardRotation rotation = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 400, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.RotateActiveInstance(rotation, out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.ExpireInstance(request));
        }

        [PostgreSQLIntegrationFact]
        public async Task RotateActiveInstance_CreatesDeterministicNextInstanceTopologyAndExactReplay()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Expired);
            await InsertDefinitionAsync(fixture, 2);
            await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Active);
            LeaderboardRotation request = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 400, true), LeaderboardState.eLBS_Created,
                [new LeaderboardMetaMapping(1, 11, 2, 20)]);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.RotateActiveInstance(request, out DBLeaderboardInstance committed));
            Assert.Equal(11, committed.InstanceId);
            committed.Visible = false;
            Assert.Equal(11L, await ScalarAsync(fixture, "SELECT active_instance_id FROM mhserveremu.leaderboard WHERE leaderboard_id = 1"));
            Assert.Equal(1L, await ScalarAsync(fixture,
                "SELECT COUNT(*) FROM mhserveremu.leaderboard_meta_entry WHERE leaderboard_id = 1 AND instance_id = 11"));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.RotateActiveInstance(request, out DBLeaderboardInstance replay));
            Assert.NotSame(committed, replay);
            Assert.Equal(11, replay.InstanceId);
            LeaderboardRotation conflict = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 401, true), LeaderboardState.eLBS_Created,
                [new LeaderboardMetaMapping(1, 11, 2, 20)]);
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Leaderboards.RotateActiveInstance(conflict, out DBLeaderboardInstance missing));
            Assert.Null(missing);
        }

        [PostgreSQLIntegrationFact]
        public async Task LifecycleWrites_PreCommitBackendTerminationRollsBackWithoutFatalFailureAndRecoversPool()
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Created);
            await InsertDefinitionAsync(fixture, 2);
            await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Active);
            await InsertDefinitionAsync(fixture, 3);
            await InsertInstanceAsync(fixture, 30, 3, true, LeaderboardState.eLBS_Active);
            await InsertDefinitionAsync(fixture, 4);
            await InsertInstanceAsync(fixture, 40, 4, true, LeaderboardState.eLBS_Expired);
            try
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(TerminateBackendBeforeCommitAsync);

                Assert.Equal(LeaderboardStoreResult.Failed, fixture.Leaderboards.ActivateInstance(new(1, 10, 10)));
                Assert.Equal(LeaderboardStoreResult.Failed, fixture.Leaderboards.SaveScoreBatch(new(2, 20, LeaderboardState.eLBS_Active,
                    [new LeaderboardEntryWrite(20, 200, 1, 2, [1])])));
                Assert.Equal(LeaderboardStoreResult.Failed, fixture.Leaderboards.ExpireInstance(new(3, 30, 30, LeaderboardState.eLBS_Active,
                    [new LeaderboardEntryWrite(30, 300, 1, 2, [1])])));
                Assert.Equal(LeaderboardStoreResult.Failed, fixture.Leaderboards.RotateActiveInstance(new(4, 40, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                    new LeaderboardInstanceSpec(41, 4, LeaderboardState.eLBS_Created, 400, true), LeaderboardState.eLBS_Created,
                    Array.Empty<LeaderboardMetaMapping>()), out _));
            }
            finally
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(null);
            }

            Assert.Empty(failures);
            Assert.Equal((short)LeaderboardState.eLBS_Created, await ScalarAsync(fixture, "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 10"));
            Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM mhserveremu.leaderboard_entry WHERE instance_id IN (20, 30)"));
            Assert.Equal((short)LeaderboardState.eLBS_Active, await ScalarAsync(fixture, "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 30"));
            Assert.Equal(40L, await ScalarAsync(fixture, "SELECT active_instance_id FROM mhserveremu.leaderboard WHERE leaderboard_id = 4"));
            Assert.Equal(0L, await ScalarAsync(fixture, "SELECT COUNT(*) FROM mhserveremu.leaderboard_instance WHERE instance_id = 41"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadInstance(1, 10, out _));
        }

        [PostgreSQLIntegrationFact]
        public async Task LifecycleWrites_CommitBackendTerminationReportsOneSanitizedFatalFailurePerWrite()
        {
            await AssertCommitTerminationAsync("activation", "leaderboard_instance", "UPDATE", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 1);
                await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Created);
            }, fixture => fixture.Leaderboards.ActivateInstance(new(1, 10, 10)));
            await AssertCommitTerminationAsync("score", "leaderboard_entry", "INSERT", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 2);
                await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Active);
            }, fixture => fixture.Leaderboards.SaveScoreBatch(new(2, 20, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(20, 200, 1, 2, [1])])));
            await AssertCommitTerminationAsync("expiration", "leaderboard_instance", "UPDATE", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 3);
                await InsertInstanceAsync(fixture, 30, 3, true, LeaderboardState.eLBS_Active);
            }, fixture => fixture.Leaderboards.ExpireInstance(new(3, 30, 30, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(30, 300, 1, 2, [1])])));
            await AssertCommitTerminationAsync("rotation", "leaderboard_instance", "INSERT", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 4);
                await InsertInstanceAsync(fixture, 40, 4, true, LeaderboardState.eLBS_Expired);
            }, fixture => fixture.Leaderboards.RotateActiveInstance(new(4, 40, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(41, 4, LeaderboardState.eLBS_Created, 400, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>()), out _));
        }

        [PostgreSQLIntegrationFact]
        public async Task MaintainVisibility_FencesBoundedNormalWindowAndRetainsRewardBearingArchives()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 1, 1, true, LeaderboardState.eLBS_Active);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await InsertInstanceAsync(fixture, 11, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await InsertInstanceAsync(fixture, 12, 1, false, LeaderboardState.eLBS_Rewarded, false);
            await InsertInstanceAsync(fixture, 13, 1, false, LeaderboardState.eLBS_Rewarded, false);
            await InsertEntryAsync(fixture, 10, 100, [1]);
            await InsertEntryAsync(fixture, 11, 101, [1]);
            await InsertEntryAsync(fixture, 13, 103, [1]);
            await InsertRewardAsync(fixture, 1, 12, 102, 1, 300, null);
            await InsertRewardAsync(fixture, 1, 13, 103, 1, 301, 400);
            await InsertDefinitionAsync(fixture, 2);
            await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Active);
            await InsertMappingAsync(fixture, 11, 1, 2, 20);
            await InsertMappingAsync(fixture, 12, 1, 2, 20);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.MaintainVisibility(new(1, 1, 500), out LeaderboardVisibilitySnapshot snapshot));

            Assert.False(await ReadInstanceVisibleAsync(fixture, 10));
            Assert.True(await ReadInstanceVisibleAsync(fixture, 11));
            Assert.True(await ReadInstanceVisibleAsync(fixture, 12));
            Assert.True(await ReadInstanceVisibleAsync(fixture, 13));
            Assert.Equal(new[] { 11L }, snapshot.NormalArchiveInstances.Select(instance => instance.InstanceId));
            Assert.Equal(new[] { (1L, 11L, 2L, 20L) }, snapshot.MetaMappings.Select(mapping =>
                (mapping.LeaderboardId, mapping.InstanceId, mapping.SubLeaderboardId, mapping.SubInstanceId)));
        }

        [PostgreSQLIntegrationFact]
        public async Task MaintainVisibility_PreCommitTerminationRollsBackVisibilityAndReturnsEmptySnapshot()
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 1, 1, true, LeaderboardState.eLBS_Active);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded, false);
            bool hookInvoked = false;
            try
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(async (connection, transaction) =>
                {
                    hookInvoked = true;
                    await TerminateBackendBeforeCommitAsync(connection, transaction);
                });
                Assert.Equal(LeaderboardStoreResult.Failed, fixture.Leaderboards.MaintainVisibility(new(1, 0, 500), out LeaderboardVisibilitySnapshot snapshot));
                Assert.Empty(snapshot.NormalArchiveInstances);
                Assert.Empty(snapshot.MetaMappings);
            }
            finally
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(null);
            }

            Assert.True(hookInvoked);
            Assert.Empty(failures);
            Assert.True(await ReadInstanceVisibleAsync(fixture, 10));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(1, 0, 10, out _));
        }

        [PostgreSQLIntegrationFact]
        public async Task MaintainVisibility_CommitTerminationReturnsOutcomeUncertainWithOneSanitizedFatalFailure()
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 1, 1, true, LeaderboardState.eLBS_Active);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded, false);
            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource, "visibility", "leaderboard_instance", "UPDATE");
            try
            {
                Assert.Equal(LeaderboardStoreResult.OutcomeUncertain, fixture.Leaderboards.MaintainVisibility(new(1, 0, 500), out LeaderboardVisibilitySnapshot snapshot));
                Assert.Empty(snapshot.NormalArchiveInstances);
                Assert.Empty(snapshot.MetaMappings);
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource, "visibility", "leaderboard_instance");
            }

            AssertSanitizedUncertainFailure(Assert.Single(failures));
        }

        [PostgreSQLIntegrationFact]
        public async Task GenerateRewards_PersistsCompleteSetWithExactReplayAndPreservedCreationDates()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Expired);
            LeaderboardRewardGeneration request = Rewards(1, 10,
                new LeaderboardRewardWrite(1, 10, 100, 20, 1, 300),
                new LeaderboardRewardWrite(1, 10, 101, 21, 2, 301));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.GenerateRewards(request));
            Assert.Equal((short)LeaderboardState.eLBS_Rewarded, await ScalarAsync(fixture,
                "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 10"));
            Assert.Equal(2L, await ScalarAsync(fixture,
                "SELECT COUNT(*) FROM mhserveremu.leaderboard_reward WHERE leaderboard_id = 1 AND instance_id = 10"));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.GenerateRewards(Rewards(1, 10,
                new LeaderboardRewardWrite(1, 10, 101, 21, 2, 901),
                new LeaderboardRewardWrite(1, 10, 100, 20, 1, 900))));
            Assert.Equal(300L, await ScalarAsync(fixture,
                "SELECT creation_date FROM mhserveremu.leaderboard_reward WHERE participant_id = 20"));

            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Leaderboards.GenerateRewards(Rewards(1, 10,
                new LeaderboardRewardWrite(1, 10, 100, 20, 1, 300))));
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Leaderboards.GenerateRewards(Rewards(1, 10,
                new LeaderboardRewardWrite(1, 10, 999, 20, 1, 300),
                new LeaderboardRewardWrite(1, 10, 101, 21, 2, 301))));
        }

        [PostgreSQLIntegrationFact]
        public async Task GenerateRewards_RejectsDuplicateParticipantsWithoutTransitioningState()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Expired);
            LeaderboardRewardGeneration request = Rewards(1, 10, new LeaderboardRewardWrite(1, 10, 100, 20, 1, 300));
            FieldInfo rewards = typeof(LeaderboardRewardGeneration).GetField("<Rewards>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(rewards);
            rewards.SetValue(request, new[]
            {
                new LeaderboardRewardWrite(1, 10, 100, 20, 1, 300),
                new LeaderboardRewardWrite(1, 10, 101, 20, 2, 301),
            });

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Leaderboards.GenerateRewards(request));
            Assert.Equal((short)LeaderboardState.eLBS_Expired, await ScalarAsync(fixture,
                "SELECT state FROM mhserveremu.leaderboard_instance WHERE instance_id = 10"));
            Assert.Equal(0L, await ScalarAsync(fixture,
                "SELECT COUNT(*) FROM mhserveremu.leaderboard_reward WHERE instance_id = 10"));
        }

        [PostgreSQLIntegrationFact]
        public async Task PendingRewards_AreDetachedAndFinalizationUsesFullKeyWithoutTimestampOverwrite()
        {
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded);
            await InsertInstanceAsync(fixture, 11, 1, false, LeaderboardState.eLBS_Rewarded, false);
            await InsertRewardAsync(fixture, 1, 10, 20, 1, 300, null);
            await InsertRewardAsync(fixture, 1, 11, 20, 2, 301, 400);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.GetPendingRewards(20, out IReadOnlyList<DBRewardEntry> pending));
            DBRewardEntry reward = Assert.Single(pending);
            reward.RewardId = 999;
            Assert.Equal(1020L, await ScalarAsync(fixture,
                "SELECT reward_id FROM mhserveremu.leaderboard_reward WHERE leaderboard_id = 1 AND instance_id = 10 AND participant_id = 20"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.GetPendingRewards(999, out IReadOnlyList<DBRewardEntry> missing));
            Assert.Empty(missing);

            LeaderboardRewardKey key = new(1, 10, 20);
            Assert.Equal(RewardFinalizationResult.Failed, fixture.Leaderboards.FinalizeReward(key, 0));
            Assert.Equal(RewardFinalizationResult.Finalized, fixture.Leaderboards.FinalizeReward(key, 500));
            Assert.Equal(RewardFinalizationResult.AlreadyFinalized, fixture.Leaderboards.FinalizeReward(key, 600));
            Assert.Equal(500L, await ScalarAsync(fixture,
                "SELECT rewarded_date FROM mhserveremu.leaderboard_reward WHERE leaderboard_id = 1 AND instance_id = 10 AND participant_id = 20"));
            Assert.Equal(RewardFinalizationResult.NotFound, fixture.Leaderboards.FinalizeReward(new(2, 10, 20), 700));
            Assert.Equal(RewardFinalizationResult.NotFound, fixture.Leaderboards.FinalizeReward(new(1, 10, 999), 700));
        }

        [PostgreSQLIntegrationFact]
        public async Task FinalizeReward_PreCommitTerminationDoesNotFinalizeOrNotifyFatalFailure()
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded);
            await InsertRewardAsync(fixture, 1, 10, 20, 1, 300, null);
            bool hookInvoked = false;
            try
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(async (connection, transaction) =>
                {
                    hookInvoked = true;
                    await TerminateBackendBeforeCommitAsync(connection, transaction);
                });
                Assert.Equal(RewardFinalizationResult.Failed, fixture.Leaderboards.FinalizeReward(new(1, 10, 20), 500));
            }
            finally
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(null);
            }

            Assert.True(hookInvoked);
            Assert.Empty(failures);
            Assert.Equal(1L, await ScalarAsync(fixture,
                "SELECT COUNT(*) FROM mhserveremu.leaderboard_reward WHERE leaderboard_id = 1 AND instance_id = 10 AND participant_id = 20 AND rewarded_date IS NULL"));
        }

        [PostgreSQLIntegrationFact]
        public async Task FinalizeReward_CommitTerminationReturnsDedicatedOutcomeUncertainWithOneSanitizedFatalFailure()
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await InsertDefinitionAsync(fixture, 1);
            await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Rewarded);
            await InsertRewardAsync(fixture, 1, 10, 20, 1, 300, null);
            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource, "finalization", "leaderboard_reward", "UPDATE");
            try
            {
                Assert.Equal(RewardFinalizationResult.OutcomeUncertain, fixture.Leaderboards.FinalizeReward(new(1, 10, 20), 500));
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource, "finalization", "leaderboard_reward");
            }

            AssertSanitizedUncertainFailure(Assert.Single(failures));
        }

        [PostgreSQLIntegrationFact]
        public async Task ScoreAndRewardWrites_ClassifyPreCommitAndCommitConnectionLosses()
        {
            await AssertPreCommitTerminationAsync(async fixture =>
            {
                await InsertDefinitionAsync(fixture, 1);
                await InsertInstanceAsync(fixture, 10, 1, true, LeaderboardState.eLBS_Active);
            }, fixture => fixture.Leaderboards.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(10, 20, 1, 2, [1])])), 1);
            await AssertPreCommitTerminationAsync(async fixture =>
            {
                await InsertDefinitionAsync(fixture, 2);
                await InsertInstanceAsync(fixture, 20, 2, true, LeaderboardState.eLBS_Expired);
            }, fixture => fixture.Leaderboards.GenerateRewards(Rewards(2, 20,
                new LeaderboardRewardWrite(2, 20, 100, 30, 1, 300))), 2);

            await AssertCommitTerminationAsync("score_reward_boundary", "leaderboard_entry", "INSERT", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 4);
                await InsertInstanceAsync(fixture, 40, 4, true, LeaderboardState.eLBS_Active);
            }, fixture => fixture.Leaderboards.SaveScoreBatch(new(4, 40, LeaderboardState.eLBS_Active,
                [new LeaderboardEntryWrite(40, 50, 1, 2, [1])])));
            await AssertCommitTerminationAsync("reward", "leaderboard_reward", "INSERT", async fixture =>
            {
                await InsertDefinitionAsync(fixture, 5);
                await InsertInstanceAsync(fixture, 50, 5, true, LeaderboardState.eLBS_Expired);
            }, fixture => fixture.Leaderboards.GenerateRewards(Rewards(5, 50,
                new LeaderboardRewardWrite(5, 50, 100, 60, 1, 300))));
        }

        private static LeaderboardReconciliation Reconciliation(long leaderboardId, bool enabled = true, long startTime = 100, int maxResetCount = 0, long activationDate = 300, long currentTime = 500, int normalArchiveLimit = 2)
        {
            return new([new LeaderboardDefinitionSpec(leaderboardId, $"Leaderboard{leaderboardId}", enabled, startTime, maxResetCount)],
                [new LeaderboardInstanceSpec(0, leaderboardId, enabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded, activationDate, enabled)],
                Array.Empty<LeaderboardMetaMapping>(), currentTime, normalArchiveLimit);
        }

        private static LeaderboardRewardGeneration Rewards(long leaderboardId, long instanceId, params LeaderboardRewardWrite[] rewards)
        {
            return new(leaderboardId, instanceId, instanceId, LeaderboardState.eLBS_Expired, rewards);
        }

        private static async Task<LeaderboardStoreResult[]> ReconcileBehindGlobalLockAsync(PostgreSQLStoreTestFixture fixture, params LeaderboardReconciliation[] requests)
        {
            await using NpgsqlConnection holder = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await holder.BeginTransactionAsync();
            await using (NpgsqlCommand command = new("SELECT pg_advisory_xact_lock(@lockKey)", holder, transaction))
            {
                command.Parameters.AddWithValue("lockKey", NpgsqlDbType.Bigint, ReconciliationLockKey);
                await command.ExecuteNonQueryAsync();
            }

            Task<LeaderboardStoreResult>[] operations = requests.Select(request => Task.Run(() => fixture.Leaderboards.ReconcileSchedule(request, out _))).ToArray();
            try
            {
                await WaitForReconciliationLockWaitAsync(fixture);
                Assert.All(operations, operation => Assert.False(operation.IsCompleted));
            }
            finally
            {
                await transaction.CommitAsync();
            }
            return await Task.WhenAll(operations);
        }

        private static async Task WaitForReconciliationLockWaitAsync(PostgreSQLStoreTestFixture fixture)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                await using NpgsqlConnection connection = await fixture.Provider.DataSource.OpenConnectionAsync();
                await using NpgsqlCommand command = new("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%'", connection);
                if ((long)await command.ExecuteScalarAsync() > 0)
                    return;
                await Task.Delay(10);
            }
            throw new TimeoutException("Reconciliation did not wait for the global advisory lock.");
        }

        private async Task AssertCommitTerminationAsync(string name, string table, string operation, Func<PostgreSQLStoreTestFixture, Task> setup,
            Func<PostgreSQLStoreTestFixture, LeaderboardStoreResult> write)
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await setup(fixture);
            await CreateDeferredBackendTerminationAsync(fixture.Provider.DataSource, name, table, operation);
            try
            {
                Assert.Equal(LeaderboardStoreResult.OutcomeUncertain, write(fixture));
            }
            finally
            {
                await DropDeferredBackendTerminationAsync(fixture.Provider.DataSource, name, table);
            }

            AssertSanitizedUncertainFailure(Assert.Single(failures));
        }

        private static void AssertSanitizedUncertainFailure(PostgreSQLPersistenceFailure failure)
        {
            Assert.Equal("WriteOutcomeUncertain", failure.Code);
            Assert.DoesNotContain("termination", failure.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pg_terminate_backend", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static async Task InsertDefinitionAsync(PostgreSQLStoreTestFixture fixture, long leaderboardId, bool enabled = true)
        {
            await ExecuteAsync(fixture, @"INSERT INTO mhserveremu.leaderboard (leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count)
                VALUES (@leaderboardId, @prototypeName, NULL, @enabled, 100, 0)",
                ("leaderboardId", NpgsqlDbType.Bigint, leaderboardId), ("prototypeName", NpgsqlDbType.Text, $"Leaderboard{leaderboardId}"), ("enabled", NpgsqlDbType.Boolean, enabled));
        }

        private static async Task InsertInstanceAsync(PostgreSQLStoreTestFixture fixture, long instanceId, long leaderboardId, bool visible, LeaderboardState state = LeaderboardState.eLBS_Created, bool setActive = true)
        {
            await ExecuteAsync(fixture, @"INSERT INTO mhserveremu.leaderboard_instance (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES (@instanceId, @leaderboardId, @state, 300, @visible)",
                ("instanceId", NpgsqlDbType.Bigint, instanceId), ("leaderboardId", NpgsqlDbType.Bigint, leaderboardId),
                ("state", NpgsqlDbType.Smallint, (short)state), ("visible", NpgsqlDbType.Boolean, visible));
            if (setActive)
            {
                await ExecuteAsync(fixture, "UPDATE mhserveremu.leaderboard SET active_instance_id = @instanceId WHERE leaderboard_id = @leaderboardId",
                    ("instanceId", NpgsqlDbType.Bigint, instanceId), ("leaderboardId", NpgsqlDbType.Bigint, leaderboardId));
            }
        }

        private static Task InsertEntryAsync(PostgreSQLStoreTestFixture fixture, long instanceId, long participantId, byte[] ruleStates)
        {
            return ExecuteAsync(fixture, @"INSERT INTO mhserveremu.leaderboard_entry (instance_id, participant_id, score, high_score, rule_states)
                VALUES (@instanceId, @participantId, 100, 200, @ruleStates)",
                ("instanceId", NpgsqlDbType.Bigint, instanceId), ("participantId", NpgsqlDbType.Bigint, participantId), ("ruleStates", NpgsqlDbType.Bytea, ruleStates));
        }

        private static Task InsertRewardAsync(PostgreSQLStoreTestFixture fixture, long leaderboardId, long instanceId, long participantId, int rank,
            long creationDate, long? rewardedDate)
        {
            return ExecuteAsync(fixture, @"INSERT INTO mhserveremu.leaderboard_reward (leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date, rewarded_date)
                VALUES (@leaderboardId, @instanceId, @participantId, @rewardId, @rank, @creationDate, @rewardedDate)",
                ("leaderboardId", NpgsqlDbType.Bigint, leaderboardId), ("instanceId", NpgsqlDbType.Bigint, instanceId),
                ("participantId", NpgsqlDbType.Bigint, participantId), ("rewardId", NpgsqlDbType.Bigint, 1000 + participantId),
                ("rank", NpgsqlDbType.Integer, rank), ("creationDate", NpgsqlDbType.Bigint, creationDate),
                ("rewardedDate", NpgsqlDbType.Bigint, rewardedDate ?? (object)DBNull.Value));
        }

        private static Task InsertMappingAsync(PostgreSQLStoreTestFixture fixture, long instanceId, long leaderboardId, long subLeaderboardId, long subInstanceId)
        {
            return ExecuteAsync(fixture, @"INSERT INTO mhserveremu.leaderboard_meta_entry (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id)
                VALUES (@leaderboardId, @instanceId, @subLeaderboardId, @subInstanceId)",
                ("leaderboardId", NpgsqlDbType.Bigint, leaderboardId), ("instanceId", NpgsqlDbType.Bigint, instanceId),
                ("subLeaderboardId", NpgsqlDbType.Bigint, subLeaderboardId), ("subInstanceId", NpgsqlDbType.Bigint, subInstanceId));
        }

        private static async Task<byte[]> ReadEntryRuleStatesAsync(PostgreSQLStoreTestFixture fixture, long instanceId, long participantId)
        {
            return (byte[])await ScalarAsync(fixture, "SELECT rule_states FROM mhserveremu.leaderboard_entry WHERE instance_id = @instanceId AND participant_id = @participantId",
                ("instanceId", NpgsqlDbType.Bigint, instanceId), ("participantId", NpgsqlDbType.Bigint, participantId));
        }

        private static async Task<bool> ReadInstanceVisibleAsync(PostgreSQLStoreTestFixture fixture, long instanceId)
        {
            return (bool)await ScalarAsync(fixture, "SELECT visible FROM mhserveremu.leaderboard_instance WHERE instance_id = @instanceId",
                ("instanceId", NpgsqlDbType.Bigint, instanceId));
        }

        private static async Task ExecuteAsync(PostgreSQLStoreTestFixture fixture, string sql, params (string Name, NpgsqlDbType Type, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, NpgsqlDbType type, object value) in parameters)
                command.Parameters.AddWithValue(name, type, value);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task<object> ScalarAsync(PostgreSQLStoreTestFixture fixture, string sql, params (string Name, NpgsqlDbType Type, object Value)[] parameters)
        {
            await using NpgsqlConnection connection = await fixture.Provider.DataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new(sql, connection);
            foreach ((string name, NpgsqlDbType type, object value) in parameters)
                command.Parameters.AddWithValue(name, type, value);
            return await command.ExecuteScalarAsync();
        }

        private static async Task TerminateBackendAsync(int processId)
        {
            await using NpgsqlConnection connection = new(Environment.GetEnvironmentVariable("MHSERVEREMU_POSTGRESQL_TEST_ADMIN_CONNECTION_STRING"));
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("SELECT pg_terminate_backend(@processId)", connection);
            command.Parameters.AddWithValue("processId", processId);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task TerminateBackendBeforeCommitAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
        {
            await TerminateBackendAsync(connection.ProcessID);
            await using NpgsqlCommand command = new("SELECT 1", connection, transaction);
            await command.ExecuteScalarAsync();
        }

        private async Task AssertPreCommitTerminationAsync(Func<PostgreSQLStoreTestFixture, Task> setup,
            Func<PostgreSQLStoreTestFixture, LeaderboardStoreResult> write, long leaderboardId)
        {
            List<PostgreSQLPersistenceFailure> failures = new();
            await using PostgreSQLStoreTestFixture fixture = await PostgreSQLStoreTestFixture.StartAsync(_database, fatalCallback: failures.Add);
            await setup(fixture);
            bool hookInvoked = false;
            try
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(async (connection, transaction) =>
                {
                    hookInvoked = true;
                    await TerminateBackendBeforeCommitAsync(connection, transaction);
                });
                Assert.Equal(LeaderboardStoreResult.Failed, write(fixture));
            }
            finally
            {
                PostgreSQLLeaderboardStore.SetLifecyclePreCommitHookForTest(null);
            }

            Assert.True(hookInvoked);
            Assert.Empty(failures);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Leaderboards.LoadVisibleInstances(leaderboardId, 0, 1, out _));
        }

        private static async Task CreateDeferredBackendTerminationAsync(NpgsqlDataSource dataSource, string name, string table, string operation)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new($"CREATE FUNCTION mhserveremu.leaderboard_{name}_termination() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_terminate_backend(pg_backend_pid()); RETURN NULL; END; $$; CREATE CONSTRAINT TRIGGER leaderboard_{name}_termination AFTER {operation} ON mhserveremu.{table} DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION mhserveremu.leaderboard_{name}_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }

        private static async Task DropDeferredBackendTerminationAsync(NpgsqlDataSource dataSource, string name, string table)
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync();
            await using NpgsqlCommand command = new($"DROP TRIGGER IF EXISTS leaderboard_{name}_termination ON mhserveremu.{table}; DROP FUNCTION IF EXISTS mhserveremu.leaderboard_{name}_termination();", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
