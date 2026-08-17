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

        private static LeaderboardReconciliation Reconciliation(long leaderboardId, bool enabled = true, long startTime = 100, int maxResetCount = 0, long activationDate = 300, long currentTime = 500, int normalArchiveLimit = 2)
        {
            return new([new LeaderboardDefinitionSpec(leaderboardId, $"Leaderboard{leaderboardId}", enabled, startTime, maxResetCount)],
                [new LeaderboardInstanceSpec(0, leaderboardId, enabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded, activationDate, enabled)],
                Array.Empty<LeaderboardMetaMapping>(), currentTime, normalArchiveLimit);
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
    }
}
