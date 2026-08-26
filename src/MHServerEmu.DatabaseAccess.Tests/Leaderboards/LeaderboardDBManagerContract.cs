using Dapper;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using System.Data.SQLite;

namespace MHServerEmu.DatabaseAccess.Tests.Leaderboards
{
    public abstract class LeaderboardDBManagerContract
    {
        protected abstract ILeaderboardDBManager CreateManager(string databasePath);

        [Fact]
        public void Initialize_CreatesSchemaAndReportsNewDatabase()
        {
            using TestDatabase database = new(CreateManager);

            Assert.True(database.Manager.Initialize(out bool isNewDatabase));
            Assert.True(isNewDatabase);
            Assert.True(database.Manager.Initialize(out isNewDatabase));
            Assert.False(isNewDatabase);

            using SQLiteConnection connection = database.OpenConnection();
            string[] indexes = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'index' ORDER BY name").ToArray();
            Assert.Contains("idx_entries_instanceid", indexes);
            Assert.Contains("idx_instances_leaderboardid", indexes);
            Assert.Contains("idx_rewards_participantid", indexes);
        }

        [Fact]
        public void InsertInitialData_InsertsParentsBeforeChildren()
        {
            using TestDatabase database = new(CreateManager);
            database.Initialize();
            using (SQLiteConnection connection = database.OpenConnection())
                connection.Execute("CREATE TRIGGER require_leaderboard BEFORE INSERT ON Instances WHEN NOT EXISTS (SELECT 1 FROM Leaderboards WHERE LeaderboardId = NEW.LeaderboardId) BEGIN SELECT RAISE(FAIL, 'leaderboard must exist first'); END;");

            database.Manager.InsertInitialData([CreateLeaderboard()], [CreateInstance()], [CreateMetaEntry()]);

            Assert.Single(database.Manager.GetLeaderboards());
            Assert.NotNull(database.Manager.GetInstance(1, 10));
            Assert.Single(database.Manager.GetMetaEntries(1, 10));
        }

        [Fact]
        public void InsertInitialData_RollsBackAllDataWhenSecondMetaEntryFails()
        {
            using TestDatabase database = new(CreateManager);
            database.Initialize();
            using (SQLiteConnection connection = database.OpenConnection())
                connection.Execute("CREATE TRIGGER fail_second_meta BEFORE INSERT ON MetaEntries WHEN NEW.SubLeaderboardId = 3 BEGIN SELECT RAISE(ABORT, 'induced meta failure'); END;");

            Assert.Throws<SQLiteException>(() => database.Manager.InsertInitialData(
                [CreateLeaderboard()], [CreateInstance()], [CreateMetaEntry(2), CreateMetaEntry(3)]));

            Assert.Empty(database.Manager.GetLeaderboards());
            using SQLiteConnection assertionConnection = database.OpenConnection();
            Assert.Equal(0L, assertionConnection.ExecuteScalar<long>("SELECT COUNT(*) FROM Instances"));
            Assert.Equal(0L, assertionConnection.ExecuteScalar<long>("SELECT COUNT(*) FROM MetaEntries"));
        }

        [Fact]
        public void EmptyBatches_DoNotCreateOrConnectToDatabase()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.db");
            ILeaderboardDBManager manager = CreateManager(path);

            manager.InsertInitialData([], [], []);
            manager.InsertLeaderboards([]);
            manager.UpdateLeaderboards([]);
            manager.UpdateOrInsertInstances([]);
            manager.UpdateOrInsertEntries([]);
            manager.InsertMetaEntries([]);
            manager.InsertRewards([]);

            Assert.False(File.Exists(path));
        }

        [Fact]
        public void ModelOperations_PreserveUpdatesOrderingVisibilityAndRewards()
        {
            using TestDatabase database = new(CreateManager);
            database.Initialize();
            DBLeaderboard leaderboard = CreateLeaderboard();
            leaderboard.StartTime = long.MinValue + 5;
            leaderboard.MaxResetCount = -2;
            database.Manager.InsertLeaderboards([leaderboard]);
            Assert.Throws<SQLiteException>(() => database.Manager.InsertLeaderboards([leaderboard]));

            leaderboard.ActiveInstanceId = 11;
            leaderboard.IsEnabled = false;
            database.Manager.UpdateLeaderboards([leaderboard]);
            Assert.Equal(long.MinValue + 5, Assert.Single(database.Manager.GetLeaderboards()).StartTime);

            DBLeaderboardInstance active = CreateInstance(10, 0, true);
            DBLeaderboardInstance hidden = CreateInstance(11, 2, true);
            DBLeaderboardInstance retained = CreateInstance(12, 2, true);
            DBLeaderboardInstance recent = CreateInstance(13, 2, true);
            database.Manager.UpdateOrInsertInstances([active, hidden, retained, recent]);
            Assert.Throws<SQLiteException>(() => database.Manager.InsertInstance(CreateInstance(10, 0, true)));

            database.Manager.UpdateOrInsertEntries([
                CreateEntry(12, -6, -5),
                CreateEntry(13, -7, -4),
                CreateEntry(10, -8, -3),
                CreateEntry(10, -9, -2)]);
            List<DBLeaderboardEntry> ascending = database.Manager.GetEntries(10, true);
            Assert.Equal([-3L, -2L], ascending.Select(entry => entry.HighScore));
            Assert.Equal([-2L, -3L], database.Manager.GetEntries(10, false).Select(entry => entry.HighScore));

            DBLeaderboardEntry updatedEntry = CreateEntry(10, -8, 8);
            database.Manager.UpdateOrInsertEntries([updatedEntry]);
            Assert.Equal(8, database.Manager.GetEntries(10, false).Single(entry => entry.ParticipantId == -8).HighScore);

            DBRewardEntry reward = new(1, 12, 100, 90, -1) { CreationDate = long.MinValue + 7, RewardedDate = 1 };
            database.Manager.InsertRewards([reward]);
            Assert.Single(database.Manager.GetRewards(90));
            reward.RewardedDate = 1;
            database.Manager.UpdateReward(reward);
            Assert.Empty(database.Manager.GetRewards(90));

            List<DBLeaderboardInstance> visible = database.Manager.GetInstances(1, 1);
            Assert.Equal([10L, 13L], visible.Select(instance => instance.InstanceId));
            Assert.False(database.Manager.GetInstance(1, 11).Visible);
            Assert.True(database.Manager.GetInstance(1, 12).Visible);

            DBMetaEntry meta = CreateMetaEntry();
            database.Manager.InsertMetaEntries([meta]);
            Assert.Equal(20, database.Manager.GetSubInstanceId(1, 10, 2));
            Assert.Single(database.Manager.GetMetaEntries(1, 10));

            Assert.True(database.Manager.UpdateActiveInstanceState(1, 10, 1));
            Assert.Equal(1, (int)database.Manager.GetInstance(1, 10).State);
            Assert.False(database.Manager.UpdateActiveInstanceState(1, 99, 1));
            Assert.Equal(10, Assert.Single(database.Manager.GetLeaderboards()).ActiveInstanceId);

            active.ActivationDate = long.MaxValue - 4;
            database.Manager.UpdateInstanceActivationDate(active);
            database.Manager.UpdateInstanceState(10, 2);
            Assert.Equal(long.MaxValue - 4, database.Manager.GetInstance(1, 10).ActivationDate);
            Assert.Equal(2, (int)database.Manager.GetInstance(1, 10).State);
        }

        private static DBLeaderboard CreateLeaderboard()
        {
            return new() { LeaderboardId = 1, PrototypeName = "Test", ActiveInstanceId = 10, IsEnabled = true, StartTime = 1, MaxResetCount = 0 };
        }

        private static DBLeaderboardInstance CreateInstance(long instanceId = 10, int state = 0, bool visible = true)
        {
            return new() { InstanceId = instanceId, LeaderboardId = 1, State = (Gazillion.LeaderboardState)state, ActivationDate = instanceId, Visible = visible };
        }

        private static DBLeaderboardEntry CreateEntry(long instanceId, long participantId, long highScore)
        {
            return new() { InstanceId = instanceId, ParticipantId = participantId, Score = highScore - 1, HighScore = highScore, RuleStates = [1] };
        }

        private static DBMetaEntry CreateMetaEntry(long subLeaderboardId = 2)
        {
            return new() { LeaderboardId = 1, InstanceId = 10, SubLeaderboardId = subLeaderboardId, SubInstanceId = 20 };
        }

        private sealed class TestDatabase : IDisposable
        {
            private readonly string _path;

            public ILeaderboardDBManager Manager { get; }

            public TestDatabase(Func<string, ILeaderboardDBManager> createManager)
            {
                _path = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.db");
                Manager = createManager(_path);
            }

            public void Initialize()
            {
                Assert.True(Manager.Initialize(out _));
            }

            public SQLiteConnection OpenConnection()
            {
                SQLiteConnection connection = new($"Data Source={_path}");
                connection.Open();
                return connection;
            }

            public void Dispose()
            {
                File.Delete(_path);
            }
        }
    }
}
