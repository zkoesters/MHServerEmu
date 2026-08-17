using System.Data.SQLite;
using Gazillion;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.DatabaseAccess.Tests.SQLite
{
    public class SQLiteLeaderboardStoreTests
    {
        [Fact]
        public void Constructor_DoesNotCreateConfiguredParentOrDatabaseFile()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "nested", "Leaderboards.db");

            _ = new SQLiteLeaderboardDBManager(databasePath);

            Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
            Assert.False(File.Exists(databasePath));
        }

        [Fact]
        public void Initialize_FreshDatabaseCreatesSchemaAndIsIdempotent()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "nested", "Leaderboards.db");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.Success, store.Initialize());
            Assert.Equal(LeaderboardStoreResult.Success, store.Initialize());
            Assert.True(File.Exists(databasePath));
            Assert.Equal(1L, ReadScalar(databasePath, "PRAGMA user_version"));
            Assert.Equal(1L, ReadScalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Leaderboards'"));
        }

        [Fact]
        public void Initialize_IncompatibleDatabaseReturnsFailedWithoutRewritingIt()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "Leaderboards.db");
            SQLiteConnection.CreateFile(databasePath);
            Execute(databasePath, "PRAGMA user_version = 9; CREATE TABLE Sentinel (Id INTEGER NOT NULL PRIMARY KEY); INSERT INTO Sentinel VALUES (7);");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.Failed, store.Initialize());
            Assert.Equal(9L, ReadScalar(databasePath, "PRAGMA user_version"));
            Assert.Equal(7L, ReadScalar(databasePath, "SELECT Id FROM Sentinel"));
            Assert.Equal(0L, ReadScalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Leaderboards'"));
        }

        [Fact]
        public void LoadEntriesAndInstance_ReturnDetachedRowsAndExpectedMissingResults()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true);
            fixture.InsertEntry(10, 20, new byte[] { 1, 2, 3 });

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadEntries(10, out IReadOnlyList<DBLeaderboardEntry> entries));
            DBLeaderboardEntry entry = Assert.Single(entries);
            entry.RuleStates[0] = 99;
            Assert.Equal(new byte[] { 1, 2, 3 }, fixture.ReadEntryRuleStates(10, 20));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadInstance(1, 10, out DBLeaderboardInstance instance));
            instance.Visible = false;
            Assert.True(fixture.ReadInstanceVisible(10));

            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Store.LoadEntries(999, out IReadOnlyList<DBLeaderboardEntry> missingEntries));
            Assert.Empty(missingEntries);
            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Store.LoadInstance(1, 999, out DBLeaderboardInstance missingInstance));
            Assert.Null(missingInstance);

            fixture.InsertInstance(11, 1, visible: true);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadEntries(11, out IReadOnlyList<DBLeaderboardEntry> emptyEntries));
            Assert.Empty(emptyEntries);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void LoadVisibleInstances_InvalidLimitReturnsInvalidDataBeforeOpeningConnection(int limit)
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "missing", "Leaderboards.db");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.InvalidData, store.LoadVisibleInstances(1, 0, limit, out IReadOnlyList<DBLeaderboardInstance> instances));
            Assert.Empty(instances);
            Assert.False(Directory.Exists(Path.GetDirectoryName(databasePath)));
        }

        [Fact]
        public void LoadVisibleInstances_UsesVisibleOwnershipAndExclusiveBoundedCursor()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 150);
            fixture.InsertDefinition(2, activeInstanceId: 999);
            for (long instanceId = 1; instanceId <= 150; instanceId++)
                fixture.InsertInstance(instanceId, 1, visible: true);
            fixture.InsertInstance(151, 1, visible: false);
            fixture.InsertInstance(999, 2, visible: true);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, 0, 100, out IReadOnlyList<DBLeaderboardInstance> first));
            Assert.Equal(100, first.Count);
            Assert.Equal(150, first[0].InstanceId);
            Assert.Equal(51, first[^1].InstanceId);
            Assert.All(first, instance => Assert.Equal(1, instance.LeaderboardId));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, first[^1].InstanceId, 100, out IReadOnlyList<DBLeaderboardInstance> second));
            Assert.Equal(50, second.Count);
            Assert.Equal(50, second[0].InstanceId);
            Assert.Equal(1, second[^1].InstanceId);
        }

        [Fact]
        public void ReconcileSchedule_FreshRequestCreatesGeneratedInitialInstanceAndNoOpReplay()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            LeaderboardReconciliation request = Reconciliation(1, enabled: true, activationDate: 300);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(request, out LeaderboardSnapshot first));
            LeaderboardInstanceSpec initial = Assert.Single(first.NonterminalInstances);
            Assert.Equal(1, initial.InstanceId);
            Assert.Equal(300, initial.ActivationDate);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(request, out LeaderboardSnapshot replay));
            Assert.Equal(first.Definitions, replay.Definitions);
            Assert.Equal(first.NonterminalInstances, replay.NonterminalInstances);
            Assert.Equal(first.NormalArchiveInstances, replay.NormalArchiveInstances);
            Assert.Equal(first.MetaMappings, replay.MetaMappings);
            Assert.Equal(1L, fixture.ReadScalar("SELECT COUNT(*) FROM Instances"));
        }

        [Fact]
        public void ReconcileSchedule_UpdatesDefinitionWithoutChangingNonzeroActiveActivationDate()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, activationDate: 300), out _));
            LeaderboardReconciliation update = Reconciliation(1, startTime: 900, maxResetCount: 4, activationDate: 700);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(update, out LeaderboardSnapshot snapshot));
            Assert.Equal(900, Assert.Single(snapshot.Definitions).StartTime);
            Assert.Equal(4, Assert.Single(snapshot.Definitions).MaxResetCount);
            Assert.Equal(300, Assert.Single(snapshot.NonterminalInstances).ActivationDate);
        }

        [Fact]
        public void ReconcileSchedule_DisableThenReenableTerminalizesAndCreatesNextInstance()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, activationDate: 300), out _));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, enabled: false, activationDate: 300), out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadInstance(1, 1, out DBLeaderboardInstance disabled));
            Assert.Equal(LeaderboardState.eLBS_Rewarded, disabled.State);
            Assert.False(disabled.Visible);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, enabled: true, activationDate: 700), out LeaderboardSnapshot reenabled));
            LeaderboardInstanceSpec created = Assert.Single(reenabled.NonterminalInstances);
            Assert.Equal(2, created.InstanceId);
            Assert.Equal(700, created.ActivationDate);
            Assert.Equal(LeaderboardState.eLBS_Created, created.State);
        }

        [Fact]
        public void ReconcileSchedule_DisabledDefinitionRepairsNonterminalActiveInstance()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 1, enabled: false);
            fixture.InsertInstance(1, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, enabled: false), out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadInstance(1, 1, out DBLeaderboardInstance repaired));
            Assert.Equal(LeaderboardState.eLBS_Rewarded, repaired.State);
            Assert.False(repaired.Visible);
        }

        [Fact]
        public void ReconcileSchedule_RepairsZeroActivationDateUsingCurrentTime()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, activationDate: 0, currentTime: 500), out LeaderboardSnapshot snapshot));

            Assert.Equal(500, Assert.Single(snapshot.NonterminalInstances).ActivationDate);
        }

        [Fact]
        public void ReconcileSchedule_OrdersSignedHighBitDefinitionsAndGeneratesUnsignedInstanceIds()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            long highBitId = unchecked((long)0xABCDEF1200000042UL);
            LeaderboardReconciliation request = new(
                new[]
                {
                    new LeaderboardDefinitionSpec(highBitId, "High", true, 100, 0),
                    new LeaderboardDefinitionSpec(1, "Low", true, 100, 0),
                },
                new[]
                {
                    new LeaderboardInstanceSpec(0, highBitId, LeaderboardState.eLBS_Created, 300, true),
                    new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true),
                },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(request, out LeaderboardSnapshot snapshot));
            Assert.Equal(new[] { 1L, highBitId }, snapshot.Definitions.Select(definition => definition.LeaderboardId));
            Assert.Contains(snapshot.NonterminalInstances, instance => instance.InstanceId == unchecked((long)0xABCDEF1200000001UL));
        }

        [Fact]
        public void ReconcileSchedule_RejectsTopologyMismatchAndRollsBackEarlierDefinitionUpdate()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(Reconciliation(1, startTime: 100), out _));
            LeaderboardReconciliation invalid = new(
                new[] { new LeaderboardDefinitionSpec(1, "Leaderboard1", true, 900, 0) },
                new[] { new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true) },
                new[] { new LeaderboardMetaMapping(1, 1, 999, 1) }, 500, 2);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(invalid, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(100, fixture.ReadScalar("SELECT StartTime FROM Leaderboards WHERE LeaderboardId = 1"));
        }

        [Fact]
        public void ReconcileSchedule_RejectsInitialInstanceCollisionAndCounterOverflow()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            LeaderboardReconciliation collision = new(
                new[]
                {
                    new LeaderboardDefinitionSpec(1, "One", true, 100, 0),
                    new LeaderboardDefinitionSpec(2, "Two", true, 100, 0),
                },
                new[]
                {
                    new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true),
                    new LeaderboardInstanceSpec(0, 2, LeaderboardState.eLBS_Created, 300, true),
                },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(collision, out _));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Leaderboards"));

            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long maxInstanceId = unchecked((long)0xABCDEF12FFFFFFFFUL);
            fixture.InsertDefinition(leaderboardId, maxInstanceId, enabled: false);
            fixture.InsertInstance(maxInstanceId, leaderboardId, visible: false, state: (int)LeaderboardState.eLBS_Rewarded);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(Reconciliation(leaderboardId, enabled: true), out _));
            Assert.Equal(0, fixture.ReadScalar("SELECT IsEnabled FROM Leaderboards WHERE LeaderboardId = @id", ("@id", leaderboardId)));
        }

        [Fact]
        public void ReconcileSchedule_ReturnsCommittedNonterminalAndBoundedNormalArchiveSnapshot()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            LeaderboardReconciliation request = Reconciliation(1, normalArchiveLimit: 1);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(request, out _));
            fixture.InsertInstance(2, 1, visible: true, state: (int)LeaderboardState.eLBS_Rewarded);
            fixture.InsertInstance(3, 1, visible: true, state: (int)LeaderboardState.eLBS_Rewarded);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ReconcileSchedule(request, out LeaderboardSnapshot snapshot));
            Assert.Single(snapshot.NonterminalInstances);
            LeaderboardInstanceSpec archive = Assert.Single(snapshot.NormalArchiveInstances);
            Assert.Equal(3, archive.InstanceId);
        }

        private static LeaderboardReconciliation Reconciliation(long leaderboardId, bool enabled = true, long startTime = 100, int maxResetCount = 0, long activationDate = 300, long currentTime = 500, int normalArchiveLimit = 2)
        {
            return new(
                new[] { new LeaderboardDefinitionSpec(leaderboardId, $"Leaderboard{leaderboardId}", enabled, startTime, maxResetCount) },
                new[] { new LeaderboardInstanceSpec(0, leaderboardId, LeaderboardState.eLBS_Created, activationDate, true) },
                Array.Empty<LeaderboardMetaMapping>(), currentTime, normalArchiveLimit);
        }

        private static void Execute(string databasePath, string sql)
        {
            using SQLiteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            using SQLiteCommand command = new(sql, connection);
            command.ExecuteNonQuery();
        }

        private static long ReadScalar(string databasePath, string sql)
        {
            using SQLiteConnection connection = new($"Data Source={databasePath}");
            connection.Open();
            using SQLiteCommand command = new(sql, connection);
            return Convert.ToInt64(command.ExecuteScalar());
        }
    }

    internal sealed class SQLiteLeaderboardStoreFixture : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly string _databasePath;

        public SQLiteLeaderboardDBManager Store { get; }

        public SQLiteLeaderboardStoreFixture()
        {
            _databasePath = Path.Combine(_directory.Path, "Leaderboards.db");
            Store = new(_databasePath);
            Assert.Equal(LeaderboardStoreResult.Success, Store.Initialize());
        }

        public void InsertDefinition(long leaderboardId, long activeInstanceId, bool enabled = true, long startTime = 100, int maxResetCount = 0)
        {
            Execute("INSERT INTO Leaderboards (LeaderboardId, PrototypeName, ActiveInstanceId, IsEnabled, StartTime, MaxResetCount) VALUES (@leaderboardId, @prototypeName, @activeInstanceId, @enabled, @startTime, @maxResetCount)",
                ("@leaderboardId", leaderboardId), ("@prototypeName", $"Leaderboard{leaderboardId}"), ("@activeInstanceId", activeInstanceId), ("@enabled", enabled), ("@startTime", startTime), ("@maxResetCount", maxResetCount));
        }

        public void InsertInstance(long instanceId, long leaderboardId, bool visible, int state = 0, long activationDate = 100)
        {
            Execute("INSERT INTO Instances (InstanceId, LeaderboardId, State, ActivationDate, Visible) VALUES (@instanceId, @leaderboardId, @state, @activationDate, @visible)",
                ("@instanceId", instanceId), ("@leaderboardId", leaderboardId), ("@state", state), ("@activationDate", activationDate), ("@visible", visible));
        }

        public void InsertEntry(long instanceId, long participantId, byte[] ruleStates)
        {
            Execute("INSERT INTO Entries (InstanceId, ParticipantId, Score, HighScore, RuleStates) VALUES (@instanceId, @participantId, 3, 4, @ruleStates)",
                ("@instanceId", instanceId), ("@participantId", participantId), ("@ruleStates", ruleStates));
        }

        public byte[] ReadEntryRuleStates(long instanceId, long participantId)
        {
            using SQLiteConnection connection = OpenConnection();
            using SQLiteCommand command = new("SELECT RuleStates FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId", connection);
            command.Parameters.AddWithValue("@instanceId", instanceId);
            command.Parameters.AddWithValue("@participantId", participantId);
            return (byte[])command.ExecuteScalar();
        }

        public bool ReadInstanceVisible(long instanceId)
        {
            using SQLiteConnection connection = OpenConnection();
            using SQLiteCommand command = new("SELECT Visible FROM Instances WHERE InstanceId = @instanceId", connection);
            command.Parameters.AddWithValue("@instanceId", instanceId);
            return Convert.ToBoolean(command.ExecuteScalar());
        }

        public long ReadScalar(string sql, params (string Name, object Value)[] parameters)
        {
            using SQLiteConnection connection = OpenConnection();
            using SQLiteCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose() => _directory.Dispose();

        private void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using SQLiteConnection connection = OpenConnection();
            using SQLiteCommand command = new(sql, connection);
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        private SQLiteConnection OpenConnection()
        {
            SQLiteConnection connection = new($"Data Source={_databasePath}");
            connection.Open();
            return connection;
        }
    }
}
