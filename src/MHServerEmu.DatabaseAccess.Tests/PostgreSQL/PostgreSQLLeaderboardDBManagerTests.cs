using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLLeaderboardDBManagerTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("Host=database.example")]
        [InlineData("Database=mhserveremu")]
        [InlineData("Host=database.example;Database=mhserveremu;InvalidKeyword=value")]
        public void Constructor_InvalidConnectionString_ThrowsArgumentException(string connectionString)
        {
            Assert.Throws<ArgumentException>(() => new PostgreSQLLeaderboardDBManager(connectionString));
        }

        [Theory]
        [InlineData("08006", "connection failure")]
        [InlineData("28P01", "authentication failure")]
        [InlineData(PostgresErrorCodes.UniqueViolation, "schema uniqueness failure")]
        [InlineData("XX000", "schema or migration failure")]
        public void CreateInitializationErrorLogMessage_ClassifiesPostgreSQLFailuresWithoutExceptionDetails(string sqlState, string category)
        {
            PostgreSQLLeaderboardDBManager manager = new("Host=database.example;Port=5432;Database=mhserveremu;Username=test-user;Password=super-secret");
            PostgresException exception = new("failure containing super-secret", "ERROR", "ERROR", sqlState);

            string message = manager.CreateInitializationErrorLogMessage(exception);

            Assert.Equal($"Initialize(): PostgreSQL leaderboard error for database database.example:5432/mhserveremu: {category} (SQLSTATE {sqlState})", message);
            Assert.DoesNotContain("failure containing", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CreateInitializationErrorLogMessage_ClassifiesNonPostgreSQLFailuresWithoutExceptionDetails()
        {
            PostgreSQLLeaderboardDBManager manager = new("Host=database.example;Port=5432;Database=mhserveremu;Username=test-user;Password=super-secret");

            Assert.Equal("Initialize(): PostgreSQL leaderboard error for database database.example:5432/mhserveremu: connection failure",
                manager.CreateInitializationErrorLogMessage(new NpgsqlException("failure containing super-secret")));
            Assert.Equal("Initialize(): PostgreSQL leaderboard error for database database.example:5432/mhserveremu: schema-version failure",
                manager.CreateInitializationErrorLogMessage(new InvalidOperationException("failure containing super-secret")));
            Assert.Equal("Initialize(): PostgreSQL leaderboard error for database database.example:5432/mhserveremu: configuration failure",
                manager.CreateInitializationErrorLogMessage(new ArgumentException("failure containing super-secret")));
            Assert.Equal("Initialize(): PostgreSQL leaderboard error for database database.example:5432/mhserveremu: unexpected failure",
                manager.CreateInitializationErrorLogMessage(new Exception("failure containing super-secret")));
        }

        [PostgreSQLFact]
        public void Initialize_FreshAndCurrentSchema_ReturnsSchemaCreationState()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = new(database.ConnectionString);

            Assert.True(manager.Initialize(out bool isNewDatabase));
            Assert.True(isNewDatabase);

            Assert.True(manager.Initialize(out isNewDatabase));
            Assert.False(isNewDatabase);
        }

        [Fact]
        public void EmptyBatches_ReturnBeforeConnecting()
        {
            PostgreSQLLeaderboardDBManager manager = new("Host=database.example;Database=mhserveremu");

            manager.InsertInitialData([], [], []);
            manager.InsertLeaderboards([]);
            manager.UpdateLeaderboards([]);
            manager.UpdateOrInsertInstances([]);
            manager.UpdateOrInsertEntries([]);
            manager.InsertMetaEntries([]);
            manager.InsertRewards([]);
        }

        [PostgreSQLFact]
        public void InsertInitialData_InsertsParentsBeforeChildren()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);

            manager.InsertInitialData([CreateLeaderboard()], [CreateInstance()], [CreateMetaEntry()]);

            Assert.Single(manager.GetLeaderboards());
            Assert.NotNull(manager.GetInstance(1, 10));
            Assert.Single(manager.GetMetaEntries(1, 10));
        }

        [PostgreSQLFact]
        public void InsertInitialData_MetaFailureRollsBackAllRows()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, @"CREATE FUNCTION fail_leaderboard_meta_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.sub_leaderboard_id = 3 THEN
                        RAISE EXCEPTION 'induced meta failure';
                    END IF;
                    RETURN NEW;
                END;
                $$");
            ExecuteNonQuery(connection, "CREATE TRIGGER fail_leaderboard_meta_insert_trigger BEFORE INSERT ON leaderboard_meta_entry FOR EACH ROW EXECUTE FUNCTION fail_leaderboard_meta_insert()");

            try
            {
                Assert.Throws<PostgresException>(() => manager.InsertInitialData(
                    [CreateLeaderboard()], [CreateInstance()], [CreateMetaEntry(2), CreateMetaEntry(3)]));

                Assert.Empty(manager.GetLeaderboards());
                Assert.Equal(0, GetCount(connection, "leaderboard_instance"));
                Assert.Equal(0, GetCount(connection, "leaderboard_meta_entry"));
            }
            finally
            {
                ExecuteNonQuery(connection, "DROP TRIGGER fail_leaderboard_meta_insert_trigger ON leaderboard_meta_entry");
                ExecuteNonQuery(connection, "DROP FUNCTION fail_leaderboard_meta_insert()");
            }
        }

        [PostgreSQLFact]
        public void LeaderboardUpdatesAndActiveTransitionPreserveSQLiteSemantics()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard();
            leaderboard.StartTime = long.MinValue + 5;
            leaderboard.MaxResetCount = -2;
            manager.InsertLeaderboards([leaderboard]);
            Assert.Throws<PostgresException>(() => manager.InsertLeaderboards([leaderboard]));

            leaderboard.ActiveInstanceId = 11;
            leaderboard.IsEnabled = false;
            manager.UpdateLeaderboards([leaderboard]);
            DBLeaderboard updated = Assert.Single(manager.GetLeaderboards());
            Assert.Equal(11, updated.ActiveInstanceId);
            Assert.False(updated.IsEnabled);
            Assert.Equal(long.MinValue + 5, updated.StartTime);
            Assert.Equal(-2, updated.MaxResetCount);

            manager.UpdateOrInsertInstances([CreateInstance(10), CreateInstance(11)]);
            Assert.True(manager.UpdateActiveInstanceState(1, 10, 1));
            Assert.False(manager.UpdateActiveInstanceState(1, 99, 1));
            Assert.Equal(10, Assert.Single(manager.GetLeaderboards()).ActiveInstanceId);

            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, @"CREATE FUNCTION fail_leaderboard_instance_state_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'induced state failure';
                END;
                $$");
            ExecuteNonQuery(connection, "CREATE TRIGGER fail_leaderboard_instance_state_update_trigger BEFORE UPDATE OF state ON leaderboard_instance FOR EACH ROW EXECUTE FUNCTION fail_leaderboard_instance_state_update()");
            try
            {
                Assert.Throws<PostgresException>(() => manager.UpdateActiveInstanceState(1, 11, 1));
                Assert.Equal(10, Assert.Single(manager.GetLeaderboards()).ActiveInstanceId);
            }
            finally
            {
                ExecuteNonQuery(connection, "DROP TRIGGER fail_leaderboard_instance_state_update_trigger ON leaderboard_instance");
                ExecuteNonQuery(connection, "DROP FUNCTION fail_leaderboard_instance_state_update()");
            }
        }

        [PostgreSQLFact]
        public void UpdateLeaderboards_LaterFailureRollsBackEarlierUpdates()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard first = CreateLeaderboard();
            DBLeaderboard second = CreateLeaderboard(2);
            manager.InsertLeaderboards([first, second]);
            first.ActiveInstanceId = 11;
            second.ActiveInstanceId = 21;

            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, @"CREATE FUNCTION fail_later_leaderboard_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.leaderboard_id = 2 THEN
                        RAISE EXCEPTION 'induced leaderboard update failure';
                    END IF;
                    RETURN NEW;
                END;
                $$");
            ExecuteNonQuery(connection, "CREATE TRIGGER fail_later_leaderboard_update_trigger BEFORE UPDATE ON leaderboard FOR EACH ROW EXECUTE FUNCTION fail_later_leaderboard_update()");
            try
            {
                Assert.Throws<PostgresException>(() => manager.UpdateLeaderboards([first, second]));

                DBLeaderboard[] leaderboards = manager.GetLeaderboards();
                Assert.Equal(10, leaderboards.Single(leaderboard => leaderboard.LeaderboardId == 1).ActiveInstanceId);
                Assert.Equal(10, leaderboards.Single(leaderboard => leaderboard.LeaderboardId == 2).ActiveInstanceId);
            }
            finally
            {
                ExecuteNonQuery(connection, "DROP TRIGGER fail_later_leaderboard_update_trigger ON leaderboard");
                ExecuteNonQuery(connection, "DROP FUNCTION fail_later_leaderboard_update()");
            }
        }

        [PostgreSQLFact]
        public void InstanceAndEntryOperationsPreserveKeysOrderingAndVisibility()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(), CreateLeaderboard(2)]);
            DBLeaderboardInstance active = CreateInstance(10, 0, true);
            DBLeaderboardInstance hidden = CreateInstance(11, 2, true);
            DBLeaderboardInstance retained = CreateInstance(12, 2, true);
            DBLeaderboardInstance recent = CreateInstance(13, 2, true);
            manager.UpdateOrInsertInstances([active, hidden, retained, recent]);
            Assert.Throws<PostgresException>(() => manager.InsertInstance(CreateInstance(10)));

            DBLeaderboardInstance updated = CreateInstance(10, 1, false);
            updated.LeaderboardId = 2;
            updated.ActivationDate = long.MaxValue - 4;
            manager.UpdateOrInsertInstances([updated]);
            DBLeaderboardInstance stored = manager.GetInstance(1, 10);
            Assert.Equal(1, (int)stored.State);
            Assert.False(stored.Visible);
            Assert.Equal(long.MaxValue - 4, stored.ActivationDate);
            Assert.Null(manager.GetInstance(2, 10));

            manager.UpdateInstanceState(10, 0);
            manager.UpdateInstanceActivationDate(updated);
            Assert.Equal(0, (int)manager.GetInstance(1, 10).State);

            manager.UpdateOrInsertEntries([
                CreateEntry(12, long.MinValue + 1, -5, [1, 2]),
                CreateEntry(13, -7, -4, [3]),
                CreateEntry(10, -8, -3, [4]),
                CreateEntry(10, -9, -2, [5])]);
            Assert.Equal([-3L, -2L], manager.GetEntries(10, true).Select(entry => entry.HighScore));
            Assert.Equal([-2L, -3L], manager.GetEntries(10, false).Select(entry => entry.HighScore));

            DBLeaderboardEntry entryUpdate = CreateEntry(10, -8, long.MaxValue - 2, [9, 8, 7]);
            entryUpdate.Score = long.MinValue + 2;
            manager.UpdateOrInsertEntries([entryUpdate]);
            DBLeaderboardEntry persistedEntry = manager.GetEntries(10, false).Single(entry => entry.ParticipantId == -8);
            Assert.Equal(long.MinValue + 2, persistedEntry.Score);
            Assert.Equal(long.MaxValue - 2, persistedEntry.HighScore);
            Assert.Equal([9, 8, 7], persistedEntry.RuleStates);

            DBRewardEntry retainedReward = CreateReward(1, 12, 90);
            manager.InsertRewards([retainedReward]);
            retainedReward.RewardedDate = 1;
            manager.UpdateReward(retainedReward);
            List<DBLeaderboardInstance> visible = manager.GetInstances(1, 1);
            Assert.Equal([10L, 13L], visible.Select(instance => instance.InstanceId));
            Assert.False(manager.GetInstance(1, 11).Visible);
            Assert.True(manager.GetInstance(1, 12).Visible);
        }

        [PostgreSQLFact]
        public void GetInstances_LaterVisibilityFailureRollsBackEarlierVisibilityUpdate()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard()]);
            manager.UpdateOrInsertInstances([
                CreateInstance(10, 2, true),
                CreateInstance(11, 2, true),
                CreateInstance(12, 2, true)]);
            manager.UpdateOrInsertEntries([
                CreateEntry(11, 1, 1, [1]),
                CreateEntry(12, 1, 1, [1])]);

            using NpgsqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, @"CREATE FUNCTION fail_later_visibility_update() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.instance_id = 11 THEN
                        RAISE EXCEPTION 'induced visibility update failure';
                    END IF;
                    RETURN NEW;
                END;
                $$");
            ExecuteNonQuery(connection, "CREATE TRIGGER fail_later_visibility_update_trigger BEFORE UPDATE OF visible ON leaderboard_instance FOR EACH ROW EXECUTE FUNCTION fail_later_visibility_update()");
            try
            {
                Assert.Throws<PostgresException>(() => manager.GetInstances(1, 1));

                Assert.True(manager.GetInstance(1, 10).Visible);
            }
            finally
            {
                ExecuteNonQuery(connection, "DROP TRIGGER fail_later_visibility_update_trigger ON leaderboard_instance");
                ExecuteNonQuery(connection, "DROP FUNCTION fail_later_visibility_update()");
            }
        }

        [PostgreSQLFact]
        public void MetaAndRewardOperationsUseSchemaForeignKeysAndExactCompositeUpdates()
        {
            using PostgreSQLTestDatabase database = new();
            PostgreSQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard()]);
            manager.UpdateOrInsertInstances([CreateInstance()]);

            DBMetaEntry meta = CreateMetaEntry(2);
            meta.InstanceId = 999;
            manager.InsertMetaEntries([meta]);
            Assert.Equal(20, manager.GetSubInstanceId(1, 999, 2));
            Assert.Equal(0, manager.GetSubInstanceId(1, 999, 3));
            Assert.Single(manager.GetMetaEntries(1, 999));
            Assert.Throws<PostgresException>(() => manager.InsertMetaEntries([meta]));

            DBRewardEntry first = CreateReward(999, 10, 90);
            DBRewardEntry second = CreateReward(1, 10, 90);
            second.RewardId = long.MinValue + 3;
            manager.InsertRewards([first, second]);
            Assert.Throws<PostgresException>(() => manager.InsertRewards([first]));
            Assert.Equal(2, manager.GetRewards(90).Count);

            first.RewardedDate = long.MaxValue - 1;
            manager.UpdateReward(first);
            DBRewardEntry remaining = Assert.Single(manager.GetRewards(90));
            Assert.Equal(second.LeaderboardId, remaining.LeaderboardId);
            Assert.Equal(second.RewardId, remaining.RewardId);
        }

        private static PostgreSQLLeaderboardDBManager Initialize(PostgreSQLTestDatabase database)
        {
            PostgreSQLLeaderboardDBManager manager = new(database.ConnectionString);
            Assert.True(manager.Initialize(out _));
            return manager;
        }

        private static DBLeaderboard CreateLeaderboard(long leaderboardId = 1)
        {
            return new()
            {
                LeaderboardId = leaderboardId,
                PrototypeName = $"Test{leaderboardId}",
                ActiveInstanceId = 10,
                IsEnabled = true,
                StartTime = 1,
                MaxResetCount = 0
            };
        }

        private static DBLeaderboardInstance CreateInstance(long instanceId = 10, int state = 0, bool visible = true)
        {
            return new()
            {
                InstanceId = instanceId,
                LeaderboardId = 1,
                State = (Gazillion.LeaderboardState)state,
                ActivationDate = instanceId,
                Visible = visible
            };
        }

        private static DBLeaderboardEntry CreateEntry(long instanceId, long participantId, long highScore, byte[] ruleStates)
        {
            return new()
            {
                InstanceId = instanceId,
                ParticipantId = participantId,
                Score = highScore - 1,
                HighScore = highScore,
                RuleStates = ruleStates
            };
        }

        private static DBMetaEntry CreateMetaEntry(long subLeaderboardId = 2)
        {
            return new()
            {
                LeaderboardId = 1,
                InstanceId = 10,
                SubLeaderboardId = subLeaderboardId,
                SubInstanceId = 20
            };
        }

        private static DBRewardEntry CreateReward(long leaderboardId, long instanceId, long participantId)
        {
            return new()
            {
                LeaderboardId = leaderboardId,
                InstanceId = instanceId,
                ParticipantId = participantId,
                RewardId = long.MinValue + 2,
                Rank = -1,
                CreationDate = long.MinValue + 7,
                RewardedDate = 0
            };
        }

        private static int GetCount(NpgsqlConnection connection, string tableName)
        {
            using NpgsqlCommand command = new($"SELECT COUNT(*) FROM {tableName}", connection);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static void ExecuteNonQuery(NpgsqlConnection connection, string commandText)
        {
            using NpgsqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }
    }
}
