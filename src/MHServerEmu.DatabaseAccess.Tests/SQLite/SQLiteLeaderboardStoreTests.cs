using System.Data.SQLite;
using System.Reflection;
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
        public void Initialize_VersionOneDatabaseWithMalformedSchemaReturnsFailedWithoutRewritingIt()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "Leaderboards.db");
            SQLiteConnection.CreateFile(databasePath);
            Execute(databasePath, "PRAGMA user_version = 1; CREATE TABLE Sentinel (Id INTEGER NOT NULL PRIMARY KEY); INSERT INTO Sentinel VALUES (7);");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.Failed, store.Initialize());
            Assert.Equal(1L, ReadScalar(databasePath, "PRAGMA user_version"));
            Assert.Equal(7L, ReadScalar(databasePath, "SELECT Id FROM Sentinel"));
            Assert.Equal(0L, ReadScalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Leaderboards'"));
        }

        [Fact]
        public void Initialize_VersionOneDatabaseWithMatchingNamesButMissingKeysAndForeignKeysReturnsFailedWithoutRewritingIt()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "Leaderboards.db");
            SQLiteConnection.CreateFile(databasePath);
            Execute(databasePath, @"
                PRAGMA user_version = 1;
                CREATE TABLE Leaderboards (LeaderboardId INTEGER NOT NULL PRIMARY KEY, PrototypeName TEXT, ActiveInstanceId INTEGER, IsEnabled INTEGER, StartTime INTEGER, MaxResetCount INTEGER);
                CREATE TABLE Instances (InstanceId INTEGER NOT NULL PRIMARY KEY, LeaderboardId INTEGER NOT NULL, State INTEGER, ActivationDate INTEGER, Visible INTEGER);
                CREATE TABLE Entries (InstanceId INTEGER NOT NULL, ParticipantId INTEGER NOT NULL, Score INTEGER, HighScore INTEGER, RuleStates BLOB);
                CREATE TABLE MetaEntries (LeaderboardId INTEGER NOT NULL, InstanceId INTEGER NOT NULL, SubLeaderboardId INTEGER NOT NULL, SubInstanceId INTEGER NOT NULL);
                CREATE TABLE Rewards (LeaderboardId INTEGER NOT NULL, InstanceId INTEGER NOT NULL, ParticipantId INTEGER NOT NULL, Rank INTEGER NOT NULL, RewardId INTEGER NOT NULL, CreationDate INTEGER, RewardedDate INTEGER);
                CREATE INDEX idx_instances_leaderboardid ON Instances (LeaderboardId);
                CREATE UNIQUE INDEX idx_entries_instanceid ON Entries (InstanceId);
                CREATE INDEX idx_rewards_participantid ON Rewards (ParticipantId);");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.Failed, store.Initialize());
            Assert.Equal(1L, ReadScalar(databasePath, "PRAGMA user_version"));
            Assert.Equal(0L, ReadScalar(databasePath, "SELECT COUNT(*) FROM pragma_foreign_key_list('Instances')"));
            Assert.Equal(0L, ReadScalar(databasePath, "SELECT COUNT(*) FROM pragma_foreign_key_list('Entries')"));
            Assert.Equal(0L, ReadScalar(databasePath, "SELECT COUNT(*) FROM pragma_index_list('Entries') WHERE name = 'idx_entries_instanceid' AND [unique] = 0"));
        }

        [Fact]
        public void Initialize_VersionOneDatabaseWithUnexpectedTriggerReturnsFailedWithoutRewritingIt()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "Leaderboards.db");
            SQLiteLeaderboardDBManager firstStore = new(databasePath);
            Assert.Equal(LeaderboardStoreResult.Success, firstStore.Initialize());
            Execute(databasePath, "CREATE TRIGGER SuppressLeaderboardInsert BEFORE INSERT ON Leaderboards BEGIN SELECT RAISE(IGNORE); END;");
            SQLiteLeaderboardDBManager store = new(databasePath);

            Assert.Equal(LeaderboardStoreResult.Failed, store.Initialize());
            Assert.Equal(1L, ReadScalar(databasePath, "PRAGMA user_version"));
            Assert.Equal(1L, ReadScalar(databasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'SuppressLeaderboardInsert'"));
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
        public void LoadVisibleInstances_PaginatesMixedSignedIdsAsExclusiveUnsignedCursor()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: -1);
            long[] expected = { -1, -2, long.MinValue, 7, 1 };
            foreach (long instanceId in expected)
                fixture.InsertInstance(instanceId, 1, visible: true);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, 0, 3, out IReadOnlyList<DBLeaderboardInstance> first));
            Assert.Equal(expected.Take(3), first.Select(instance => instance.InstanceId));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, first[^1].InstanceId, 3, out IReadOnlyList<DBLeaderboardInstance> second));
            Assert.Equal(expected.Skip(3), second.Select(instance => instance.InstanceId));
            Assert.Equal(expected, first.Concat(second).Select(instance => instance.InstanceId));
        }

        [Fact]
        public void LoadVisibleInstances_PaginatesMixedSignedIdsAcrossNegativeAndPositiveCursorsWithoutRepeats()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: -1);
            long[] expected = { -1, -2, long.MinValue, 7, 3, 1 };
            foreach (long instanceId in expected)
                fixture.InsertInstance(instanceId, 1, visible: true);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, 0, 2, out IReadOnlyList<DBLeaderboardInstance> first));
            Assert.Equal(expected.Take(2), first.Select(instance => instance.InstanceId));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, first[^1].InstanceId, 2, out IReadOnlyList<DBLeaderboardInstance> second));
            Assert.Equal(expected.Skip(2).Take(2), second.Select(instance => instance.InstanceId));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.LoadVisibleInstances(1, second[^1].InstanceId, 2, out IReadOnlyList<DBLeaderboardInstance> third));
            Assert.Equal(expected.Skip(4), third.Select(instance => instance.InstanceId));
            Assert.Equal(expected, first.Concat(second).Concat(third).Select(instance => instance.InstanceId));
        }

        [Fact]
        public void ActivateInstance_MovesCreatedInstanceToActiveAndReplaysExactly()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);
            LeaderboardActivation request = new(1, 10, 10);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ActivateInstance(request));
            Assert.Equal((int)LeaderboardState.eLBS_Active, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ActivateInstance(request));
        }

        [Fact]
        public void ActivateInstance_RejectsStalePointerStateAndWrongOwnershipWithoutWriting()
        {
            using SQLiteLeaderboardStoreFixture stalePointer = new();
            stalePointer.InsertDefinition(1, activeInstanceId: 11);
            stalePointer.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);
            stalePointer.InsertInstance(11, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);

            Assert.Equal(LeaderboardStoreResult.StaleState, stalePointer.Store.ActivateInstance(new(1, 10, 10)));
            Assert.Equal((int)LeaderboardState.eLBS_Created, stalePointer.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));

            using SQLiteLeaderboardStoreFixture staleState = new();
            staleState.InsertDefinition(1, activeInstanceId: 10);
            staleState.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);

            Assert.Equal(LeaderboardStoreResult.StaleState, staleState.Store.ActivateInstance(new(1, 10, 10)));

            using SQLiteLeaderboardStoreFixture wrongOwnership = new();
            wrongOwnership.InsertDefinition(1, activeInstanceId: 10);
            wrongOwnership.InsertDefinition(2, activeInstanceId: 20);
            wrongOwnership.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);
            wrongOwnership.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);

            Assert.Equal(LeaderboardStoreResult.InvalidData, wrongOwnership.Store.ActivateInstance(new(1, 10, 20)));
            Assert.Equal((int)LeaderboardState.eLBS_Created, wrongOwnership.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 20"));
        }

        [Fact]
        public void ActivateInstance_ReturnsNotFoundForMissingRowsAndInvalidDataForCorruptActiveOwnership()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();

            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Store.ActivateInstance(new(1, 10, 10)));
            fixture.InsertDefinition(1, activeInstanceId: 10);
            Assert.Equal(LeaderboardStoreResult.NotFound, fixture.Store.ActivateInstance(new(1, 10, 10)));

            fixture.InsertDefinition(2, activeInstanceId: 20);
            fixture.InsertInstance(10, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);
            fixture.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ActivateInstance(new(1, 10, 10)));
            Assert.Equal((int)LeaderboardState.eLBS_Created, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));
        }

        [Fact]
        public void SaveScoreBatch_PersistsLargeSignedBatchAndUpdatesExistingRows()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            long highBitParticipantId = unchecked((long)0xFEDCBA9876543210UL);
            List<LeaderboardEntryWrite> entries = Enumerable.Range(0, 1000)
                .Select(index => new LeaderboardEntryWrite(10, index == 999 ? highBitParticipantId : index + 1,
                    index == 999 ? long.MinValue : index, index == 999 ? long.MaxValue : index + 1, new[] { (byte)(index % 251), (byte)(index / 251) }))
                .ToList();
            LeaderboardScoreBatch request = new(1, 10, LeaderboardState.eLBS_Active, entries);

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.SaveScoreBatch(request));
            Assert.Equal(1000, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
            Assert.Equal(long.MinValue, fixture.ReadScalar("SELECT Score FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId",
                ("@instanceId", 10L), ("@participantId", highBitParticipantId)));
            Assert.Equal(long.MaxValue, fixture.ReadScalar("SELECT HighScore FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId",
                ("@instanceId", 10L), ("@participantId", highBitParticipantId)));
            Assert.Equal(new byte[] { 246, 3 }, fixture.ReadEntryRuleStates(10, highBitParticipantId));

            LeaderboardScoreBatch replacement = new(1, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, highBitParticipantId, long.MaxValue, long.MinValue, new byte[] { 9, 8, 7 }) });
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.SaveScoreBatch(replacement));
            Assert.Equal(1000, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
            Assert.Equal(long.MaxValue, fixture.ReadScalar("SELECT Score FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId",
                ("@instanceId", 10L), ("@participantId", highBitParticipantId)));
            Assert.Equal(long.MinValue, fixture.ReadScalar("SELECT HighScore FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId",
                ("@instanceId", 10L), ("@participantId", highBitParticipantId)));
            Assert.Equal(new byte[] { 9, 8, 7 }, fixture.ReadEntryRuleStates(10, highBitParticipantId));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.SaveScoreBatch(replacement));
        }

        [Fact]
        public void SaveScoreBatch_RejectsInvalidBatchWithoutPartialWrites()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 21, 1, 1, new byte[] { 1 }), new LeaderboardEntryWrite(10, 21, 2, 2, new byte[] { 2 }) })));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
        }

        [Fact]
        public void SaveScoreBatch_RejectsStaleStateAndOwnershipWhileEmptyBatchIsActiveNoOp()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Created);

            Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Store.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 30, 40, new byte[] { 1 }) })));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));

            fixture.InsertDefinition(2, activeInstanceId: 20);
            fixture.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.SaveScoreBatch(new(1, 20, LeaderboardState.eLBS_Active, Array.Empty<LeaderboardEntryWrite>())));

            fixture.Store.UpdateInstanceState(10, (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active, Array.Empty<LeaderboardEntryWrite>())));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
        }

        [Fact]
        public void SaveScoreBatch_ReturnsInvalidDataForPersistedNullRuleStates()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            fixture.InsertEntry(10, 20, null);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.SaveScoreBatch(new(1, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 30, 40, new byte[] { 1 }) })));
            Assert.Null(fixture.ReadEntryRuleStatesOrNull(10, 20));
        }

        [Fact]
        public void ExpireInstance_PersistsCompleteFinalRowsAndReplaysAfterPointerRotation()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardExpiration request = new(1, 10, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, long.MinValue, long.MaxValue, new byte[] { 1, 2, 3 }) });

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ExpireInstance(request));
            Assert.Equal((int)LeaderboardState.eLBS_Expired, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));
            Assert.Equal(1, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ExpireInstance(request));

            LeaderboardRotation rotation = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.RotateActiveInstance(rotation, out _));
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ExpireInstance(request));
        }

        [Fact]
        public void ExpireInstance_RollsBackFinalRowsForStalePointerOrState()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 11);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            fixture.InsertInstance(11, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardExpiration request = new(1, 10, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 30, 40, new byte[] { 1 }) });

            Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Store.ExpireInstance(request));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
            Assert.Equal((int)LeaderboardState.eLBS_Active, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));

            fixture.UpdateActiveInstance(1, 10);
            fixture.Store.UpdateInstanceState(10, (int)LeaderboardState.eLBS_Created);
            Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Store.ExpireInstance(request));
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Entries WHERE InstanceId = 10"));
        }

        [Fact]
        public void ExpireInstance_RejectsMismatchedPersistedFinalRow()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardExpiration request = new(1, 10, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 30, 40, new byte[] { 1 }) });

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.ExpireInstance(request));
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Store.ExpireInstance(new(1, 10, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 30, 41, new byte[] { 1 }) })));
            Assert.Equal(40, fixture.ReadScalar("SELECT HighScore FROM Entries WHERE InstanceId = 10 AND ParticipantId = 20"));
        }

        [Fact]
        public void ExpireInstance_ReturnsInvalidDataForCrossLeaderboardOwnershipAndPersistedNullRuleStates()
        {
            using SQLiteLeaderboardStoreFixture ownership = new();
            ownership.InsertDefinition(1, activeInstanceId: 10);
            ownership.InsertDefinition(2, activeInstanceId: 20);
            ownership.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            ownership.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.InvalidData, ownership.Store.ExpireInstance(new(1, 10, 20, LeaderboardState.eLBS_Active,
                Array.Empty<LeaderboardEntryWrite>())));

            using SQLiteLeaderboardStoreFixture nullRuleStates = new();
            nullRuleStates.InsertDefinition(1, activeInstanceId: 10);
            nullRuleStates.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            nullRuleStates.InsertEntry(10, 20, null);
            Assert.Equal(LeaderboardStoreResult.InvalidData, nullRuleStates.Store.ExpireInstance(new(1, 10, 10, LeaderboardState.eLBS_Active,
                new[] { new LeaderboardEntryWrite(10, 20, 3, 4, new byte[] { 1 }) })));
        }

        [Fact]
        public void RotateActiveInstance_CreatesNextInstanceMappingsAndExactReplay()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            fixture.InsertDefinition(2, activeInstanceId: 20);
            fixture.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardRotation request = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                new[] { new LeaderboardMetaMapping(1, 11, 2, 20) });

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.RotateActiveInstance(request, out DBLeaderboardInstance committed));
            Assert.Equal(11, committed.InstanceId);
            committed.Visible = false;
            Assert.Equal(11, fixture.ReadScalar("SELECT ActiveInstanceId FROM Leaderboards WHERE LeaderboardId = 1"));
            Assert.Equal((int)LeaderboardState.eLBS_Expired, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 10"));
            Assert.Equal((int)LeaderboardState.eLBS_Created, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 11"));
            Assert.Equal(1, fixture.ReadScalar("SELECT COUNT(*) FROM MetaEntries WHERE LeaderboardId = 1 AND InstanceId = 11"));
            Assert.Equal(1, fixture.ReadScalar("SELECT Visible FROM Instances WHERE InstanceId = 11"));

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.RotateActiveInstance(request, out DBLeaderboardInstance replay));
            Assert.NotSame(committed, replay);
            Assert.Equal(11, replay.InstanceId);
        }

        [Fact]
        public void RotateActiveInstance_RejectsReplayWithMismatchedNextRowOrMappings()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 10);
            fixture.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            fixture.InsertDefinition(2, activeInstanceId: 20);
            fixture.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardRotation request = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                new[] { new LeaderboardMetaMapping(1, 11, 2, 20) });
            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.RotateActiveInstance(request, out _));

            LeaderboardRotation differentNext = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 201, true), LeaderboardState.eLBS_Created,
                new[] { new LeaderboardMetaMapping(1, 11, 2, 20) });
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Store.RotateActiveInstance(differentNext, out DBLeaderboardInstance noNext));
            Assert.Null(noNext);

            LeaderboardRotation differentMappings = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());
            Assert.Equal(LeaderboardStoreResult.Conflict, fixture.Store.RotateActiveInstance(differentMappings, out _));
        }

        [Fact]
        public void RotateActiveInstance_RejectsStaleOwnershipAndInvalidTopologyWithoutPartialWrites()
        {
            using SQLiteLeaderboardStoreFixture stale = new();
            stale.InsertDefinition(1, activeInstanceId: 12);
            stale.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            stale.InsertInstance(12, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            LeaderboardRotation request = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());

            Assert.Equal(LeaderboardStoreResult.StaleState, stale.Store.RotateActiveInstance(request, out _));
            Assert.Equal(0, stale.ReadScalar("SELECT COUNT(*) FROM Instances WHERE InstanceId = 11"));

            using SQLiteLeaderboardStoreFixture wrongState = new();
            wrongState.InsertDefinition(1, activeInstanceId: 10);
            wrongState.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.StaleState, wrongState.Store.RotateActiveInstance(request, out _));
            Assert.Equal(0, wrongState.ReadScalar("SELECT COUNT(*) FROM Instances WHERE InstanceId = 11"));

            using SQLiteLeaderboardStoreFixture wrongOwnership = new();
            wrongOwnership.InsertDefinition(1, activeInstanceId: 10);
            wrongOwnership.InsertDefinition(2, activeInstanceId: 20);
            wrongOwnership.InsertInstance(10, 2, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            wrongOwnership.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            Assert.Equal(LeaderboardStoreResult.InvalidData, wrongOwnership.Store.RotateActiveInstance(request, out _));
            Assert.Equal(0, wrongOwnership.ReadScalar("SELECT COUNT(*) FROM Instances WHERE InstanceId = 11"));

            using SQLiteLeaderboardStoreFixture topology = new();
            topology.InsertDefinition(1, activeInstanceId: 10);
            topology.InsertInstance(10, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            topology.InsertDefinition(2, activeInstanceId: 20);
            topology.InsertInstance(20, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);
            LeaderboardRotation invalidTopology = new(1, 10, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(11, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                new[] { new LeaderboardMetaMapping(1, 11, 2, 21) });

            Assert.Equal(LeaderboardStoreResult.InvalidData, topology.Store.RotateActiveInstance(invalidTopology, out _));
            Assert.Equal(10, topology.ReadScalar("SELECT ActiveInstanceId FROM Leaderboards WHERE LeaderboardId = 1"));
            Assert.Equal(0, topology.ReadScalar("SELECT COUNT(*) FROM Instances WHERE InstanceId = 11"));
            Assert.Equal(0, topology.ReadScalar("SELECT COUNT(*) FROM MetaEntries WHERE LeaderboardId = 1"));
        }

        [Fact]
        public void RotateActiveInstance_RejectsGeneratedIdCollisionAndCounterOverflowWithoutWriting()
        {
            using SQLiteLeaderboardStoreFixture collision = new();
            collision.InsertDefinition(1, activeInstanceId: 1);
            collision.InsertInstance(1, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            collision.InsertDefinition(2, activeInstanceId: 2);
            collision.InsertInstance(2, 2, visible: true, state: (int)LeaderboardState.eLBS_Active);
            LeaderboardRotation collisionRequest = new(1, 1, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());

            Assert.Equal(LeaderboardStoreResult.InvalidData, collision.Store.RotateActiveInstance(collisionRequest, out _));
            Assert.Equal(1, collision.ReadScalar("SELECT ActiveInstanceId FROM Leaderboards WHERE LeaderboardId = 1"));

            using SQLiteLeaderboardStoreFixture overflow = new();
            long leaderboardId = unchecked((long)0xABCDEF1200000042UL);
            long maximumInstanceId = unchecked((long)0xABCDEF12FFFFFFFFUL);
            overflow.InsertDefinition(leaderboardId, activeInstanceId: maximumInstanceId);
            overflow.InsertInstance(maximumInstanceId, leaderboardId, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            LeaderboardRotation overflowRequest = new(leaderboardId, maximumInstanceId, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(0, leaderboardId, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());

            Assert.Equal(LeaderboardStoreResult.InvalidData, overflow.Store.RotateActiveInstance(overflowRequest, out _));
            Assert.Equal(maximumInstanceId, overflow.ReadScalar("SELECT ActiveInstanceId FROM Leaderboards WHERE LeaderboardId = @leaderboardId", ("@leaderboardId", leaderboardId)));
        }

        [Fact]
        public void RotateActiveInstance_RejectsZeroIdReplayForSameShapedDifferentActiveInstance()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 1);
            fixture.InsertInstance(1, 1, visible: true, state: (int)LeaderboardState.eLBS_Expired);
            LeaderboardRotation request = new(1, 1, LeaderboardState.eLBS_Expired, LeaderboardState.eLBS_Expired,
                new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 200, true), LeaderboardState.eLBS_Created,
                Array.Empty<LeaderboardMetaMapping>());

            Assert.Equal(LeaderboardStoreResult.Success, fixture.Store.RotateActiveInstance(request, out DBLeaderboardInstance committed));
            Assert.Equal(2, committed.InstanceId);
            fixture.InsertInstance(3, 1, visible: true, state: (int)LeaderboardState.eLBS_Created, activationDate: 200);
            fixture.UpdateActiveInstance(1, 3);

            Assert.Equal(LeaderboardStoreResult.StaleState, fixture.Store.RotateActiveInstance(request, out DBLeaderboardInstance replay));
            Assert.Null(replay);
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
        public void ReconcileSchedule_RejectsInitialInstanceWithInvalidStateBeforeWriting()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            LeaderboardReconciliation invalid = new(
                new[] { new LeaderboardDefinitionSpec(1, "Leaderboard1", true, 100, 0) },
                new[] { new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Rewarded, 300, true) },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(invalid, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Leaderboards"));
        }

        [Fact]
        public void ReconcileSchedule_RejectsInvisibleInitialInstanceForEnabledDefinitionBeforeWriting()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            LeaderboardReconciliation invalid = new(
                new[] { new LeaderboardDefinitionSpec(1, "Leaderboard1", true, 100, 0) },
                new[] { new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, false) },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(invalid, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(0, fixture.ReadScalar("SELECT COUNT(*) FROM Leaderboards"));
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
        public void ReconcileSchedule_SerializesStateReadsBeforeCompetingTerminalization()
        {
            using TemporaryDirectory directory = new();
            string databasePath = Path.Combine(directory.Path, "Leaderboards.db");
            SQLiteLeaderboardDBManager store = new(databasePath);
            Assert.Equal(LeaderboardStoreResult.Success, store.Initialize());
            Assert.Equal(LeaderboardStoreResult.Success, store.ReconcileSchedule(Reconciliation(1), out _));
            FieldInfo hookField = typeof(SQLiteLeaderboardDBManager).GetField("ReconciliationStateReadHook", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(hookField);
            bool competingWriteBlocked = false;

            try
            {
                hookField.SetValue(null, (Action)(() =>
                {
                    try
                    {
                        using SQLiteConnection connection = new($"Data Source={databasePath};Default Timeout=1");
                        connection.Open();
                        using SQLiteCommand command = new("UPDATE Instances SET State = 5, Visible = 0 WHERE InstanceId = 1", connection);
                        command.ExecuteNonQuery();
                    }
                    catch (SQLiteException)
                    {
                        competingWriteBlocked = true;
                    }
                }));

                Assert.Equal(LeaderboardStoreResult.Success, store.ReconcileSchedule(Reconciliation(1, startTime: 900), out _));
            }
            finally
            {
                hookField.SetValue(null, null);
            }

            Assert.True(competingWriteBlocked);
            Assert.Equal((int)LeaderboardState.eLBS_Created, ReadScalar(databasePath, "SELECT State FROM Instances WHERE InstanceId = 1"));
            Assert.Equal(1, ReadScalar(databasePath, "SELECT ActiveInstanceId FROM Leaderboards WHERE LeaderboardId = 1"));
            Assert.Equal(1, ReadScalar(databasePath, "SELECT IsEnabled FROM Leaderboards WHERE LeaderboardId = 1"));
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
        public void ReconcileSchedule_RejectsCrossLeaderboardActivePointerBeforeTerminalizing()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 2, enabled: true);
            fixture.InsertDefinition(2, activeInstanceId: 2, enabled: true);
            fixture.InsertInstance(2, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);
            LeaderboardReconciliation invalid = new(
                new[]
                {
                    new LeaderboardDefinitionSpec(1, "Leaderboard1", false, 100, 0),
                    new LeaderboardDefinitionSpec(2, "Leaderboard2", true, 100, 0),
                },
                new[]
                {
                    new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Rewarded, 300, false),
                    new LeaderboardInstanceSpec(0, 2, LeaderboardState.eLBS_Created, 300, true),
                },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(invalid, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(1, fixture.ReadScalar("SELECT IsEnabled FROM Leaderboards WHERE LeaderboardId = 1"));
            Assert.Equal((int)LeaderboardState.eLBS_Created, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 2"));
        }

        [Fact]
        public void ReconcileSchedule_RejectsCrossLeaderboardActivePointerForRetainedDefinitionBeforeWriting()
        {
            using SQLiteLeaderboardStoreFixture fixture = new();
            fixture.InsertDefinition(1, activeInstanceId: 2, enabled: true, startTime: 100);
            fixture.InsertDefinition(2, activeInstanceId: 2, enabled: true);
            fixture.InsertInstance(2, 2, visible: true, state: (int)LeaderboardState.eLBS_Created);
            LeaderboardReconciliation invalid = new(
                new[]
                {
                    new LeaderboardDefinitionSpec(1, "Leaderboard1", true, 900, 0),
                    new LeaderboardDefinitionSpec(2, "Leaderboard2", true, 100, 0),
                },
                new[]
                {
                    new LeaderboardInstanceSpec(0, 1, LeaderboardState.eLBS_Created, 300, true),
                    new LeaderboardInstanceSpec(0, 2, LeaderboardState.eLBS_Created, 300, true),
                },
                Array.Empty<LeaderboardMetaMapping>(), 500, 2);

            Assert.Equal(LeaderboardStoreResult.InvalidData, fixture.Store.ReconcileSchedule(invalid, out LeaderboardSnapshot snapshot));
            Assert.Empty(snapshot.Definitions);
            Assert.Equal(100, fixture.ReadScalar("SELECT StartTime FROM Leaderboards WHERE LeaderboardId = 1"));
            Assert.Equal((int)LeaderboardState.eLBS_Created, fixture.ReadScalar("SELECT State FROM Instances WHERE InstanceId = 2"));
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
                new[] { new LeaderboardInstanceSpec(0, leaderboardId, enabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded, activationDate, enabled) },
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

        public void UpdateActiveInstance(long leaderboardId, long activeInstanceId)
        {
            Execute("UPDATE Leaderboards SET ActiveInstanceId = @activeInstanceId WHERE LeaderboardId = @leaderboardId",
                ("@leaderboardId", leaderboardId), ("@activeInstanceId", activeInstanceId));
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

        public byte[] ReadEntryRuleStatesOrNull(long instanceId, long participantId)
        {
            using SQLiteConnection connection = OpenConnection();
            using SQLiteCommand command = new("SELECT RuleStates FROM Entries WHERE InstanceId = @instanceId AND ParticipantId = @participantId", connection);
            command.Parameters.AddWithValue("@instanceId", instanceId);
            command.Parameters.AddWithValue("@participantId", participantId);
            return command.ExecuteScalar() as byte[];
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
