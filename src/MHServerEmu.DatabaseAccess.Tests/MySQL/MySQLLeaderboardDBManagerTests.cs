using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLLeaderboardDBManagerTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Server=database.example")]
        [InlineData("Database=leaderboards")]
        public void Constructor_InvalidConnectionString_ThrowsArgumentException(string connectionString)
        {
            Assert.Throws<ArgumentException>(() => new MySQLLeaderboardDBManager(connectionString));
        }

        [Fact]
        public void CreateInitializationErrorLogMessage_ContainsOnlySafeDatabaseDescription()
        {
            MySQLLeaderboardDBManager manager = new("Server=database.example;Port=3307;Database=leaderboards;User ID=test-user;Password=super-secret");

            string message = manager.CreateInitializationErrorLogMessage(new InvalidOperationException("raw database failure"));

            Assert.Equal("Initialize(): MySQL leaderboard error for database database.example:3307/leaderboards: unexpected failure", message);
            Assert.DoesNotContain("test-user", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("super-secret", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("raw database failure", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Server=", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmptyBatches_ReturnWithoutOpeningOrConfiguringAConnection()
        {
            MySQLLeaderboardDBManager manager = new();

            manager.InsertInitialData([], [], []);
            manager.InsertLeaderboards([]);
            manager.UpdateLeaderboards([]);
        }

        [MySQLFact]
        public void Initialize_FreshAndCurrentDatabase_ReportExpectedCreationState()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = new(database.ConnectionString);

            Assert.True(manager.Initialize(out bool isNewDatabase));
            Assert.True(isNewDatabase);
            Assert.True(manager.Initialize(out isNewDatabase));
            Assert.False(isNewDatabase);
        }

        [MySQLFact]
        public void InsertInitialData_InsertsParentsBeforeChildren()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            DBLeaderboardInstance instance = CreateInstance(200, leaderboard.LeaderboardId);
            DBMetaEntry metaEntry = CreateMetaEntry(leaderboard.LeaderboardId, instance.InstanceId);

            manager.InsertInitialData([leaderboard], [instance], [metaEntry]);

            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(1, GetRowCount(connection, "leaderboard"));
            Assert.Equal(1, GetRowCount(connection, "leaderboard_instance"));
            Assert.Equal(1, GetRowCount(connection, "leaderboard_meta_entry"));
        }

        [MySQLFact]
        public void InsertInitialData_LateMetadataFailure_RollsBackParentsAndChildren()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            DBLeaderboardInstance instance = CreateInstance(200, leaderboard.LeaderboardId);
            DBMetaEntry metaEntry = CreateMetaEntry(leaderboard.LeaderboardId, instance.InstanceId);

            Assert.Throws<MySqlException>(() => manager.InsertInitialData([leaderboard], [instance], [metaEntry, metaEntry]));

            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(0, GetRowCount(connection, "leaderboard"));
            Assert.Equal(0, GetRowCount(connection, "leaderboard_instance"));
            Assert.Equal(0, GetRowCount(connection, "leaderboard_meta_entry"));
        }

        [MySQLFact]
        public void InsertLeaderboards_ThenGetLeaderboards_RoundTripsDefinition()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard expected = CreateLeaderboard(100);

            manager.InsertLeaderboards([expected]);

            DBLeaderboard actual = Assert.Single(manager.GetLeaderboards());
            AssertLeaderboard(expected, actual);
        }

        [MySQLFact]
        public void InsertLeaderboards_DuplicateId_PropagatesMySqlException()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            manager.InsertLeaderboards([leaderboard]);

            Assert.Throws<MySqlException>(() => manager.InsertLeaderboards([leaderboard]));
        }

        [MySQLFact]
        public void UpdateLeaderboards_UpdatesEveryMutableDefinitionField()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            manager.InsertLeaderboards([leaderboard]);
            leaderboard.PrototypeName = "UpdatedPrototype";
            leaderboard.ActiveInstanceId = 201;
            leaderboard.IsEnabled = false;
            leaderboard.StartTime = 202;
            leaderboard.MaxResetCount = 203;

            manager.UpdateLeaderboards([leaderboard]);

            AssertLeaderboard(leaderboard, Assert.Single(manager.GetLeaderboards()));
        }

        [MySQLFact]
        public void UpdateLeaderboards_BatchFailure_RollsBackEarlierUpdates()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard first = CreateLeaderboard(100);
            DBLeaderboard second = CreateLeaderboard(101);
            manager.InsertLeaderboards([first, second]);
            first.PrototypeName = "UpdatedFirst";
            second.PrototypeName = "UpdatedSecond";

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_second_leaderboard_update BEFORE UPDATE ON leaderboard FOR EACH ROW
                        IF NEW.leaderboard_id = 101 THEN
                            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced update failure';
                        END IF");

                Assert.Throws<MySqlException>(() => manager.UpdateLeaderboards([first, second]));
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_second_leaderboard_update");
            }

            DBLeaderboard[] leaderboards = manager.GetLeaderboards().OrderBy(leaderboard => leaderboard.LeaderboardId).ToArray();
            Assert.Equal("Prototype100", leaderboards[0].PrototypeName);
            Assert.Equal("Prototype101", leaderboards[1].PrototypeName);
        }

        [MySQLFact]
        public void LeaderboardOperations_WithMaxPoolSizeOne_ReuseShortLivedConnections()
        {
            using MySQLTestDatabase database = new();
            MySqlConnectionStringBuilder builder = new(database.ConnectionString) { MaximumPoolSize = 1, ConnectionTimeout = 2 };
            MySQLLeaderboardDBManager manager = new(builder.ConnectionString);
            try
            {
                Assert.True(manager.Initialize(out _));
                manager.InsertLeaderboards([CreateLeaderboard(100)]);

                for (int i = 0; i < 20; i++)
                    Assert.Single(manager.GetLeaderboards());
            }
            finally
            {
                using MySqlConnection connection = new(builder.ConnectionString);
                MySqlConnection.ClearPool(connection);
            }
        }

        [MySQLFact]
        public void UpdateLeaderboards_TransientFailure_PropagatesWithoutApplyingUpdate()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            manager.InsertLeaderboards([leaderboard]);
            leaderboard.PrototypeName = "UpdatedPrototype";

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                {
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_leaderboard_update_once BEFORE UPDATE ON leaderboard FOR EACH ROW
                        SIGNAL SQLSTATE 'HY000' SET MYSQL_ERRNO = 1205, MESSAGE_TEXT = 'induced transient update failure'");
                }

                // MySQL 9.7 GTID defaults prohibit nontransactional trigger audits. A transactional audit rolls back
                // with the induced failure, so it cannot establish an exact attempt count without invalid instrumentation.
                MySqlException exception = Assert.Throws<MySqlException>(() => manager.UpdateLeaderboards([leaderboard]));
                Assert.Equal(1205, exception.Number);
                Assert.Equal("Prototype100", Assert.Single(manager.GetLeaderboards()).PrototypeName);
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_leaderboard_update_once");
            }
        }

        [MySQLFact]
        public void InstanceOperations_RoundTripUpdatesAndPreservesExistingLeaderboard()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100), CreateLeaderboard(101)]);
            DBLeaderboardInstance instance = CreateInstance(long.MinValue + 1, 100);
            instance.State = (Gazillion.LeaderboardState)1;
            instance.ActivationDate = long.MaxValue - 1;
            instance.Visible = false;

            manager.InsertInstance(instance);
            Assert.Throws<MySqlException>(() => manager.InsertInstance(instance));

            DBLeaderboardInstance update = CreateInstance(instance.InstanceId, 101);
            update.State = (Gazillion.LeaderboardState)2;
            update.ActivationDate = long.MinValue + 2;
            update.Visible = true;
            manager.UpdateOrInsertInstances([update]);

            DBLeaderboardInstance actual = manager.GetInstance(100, instance.InstanceId);
            Assert.NotNull(actual);
            Assert.Equal(100, actual.LeaderboardId);
            Assert.Equal(update.State, actual.State);
            Assert.Equal(update.ActivationDate, actual.ActivationDate);
            Assert.Equal(update.Visible, actual.Visible);
            Assert.Null(manager.GetInstance(101, instance.InstanceId));

            manager.UpdateInstanceState(instance.InstanceId, 1);
            update.ActivationDate = long.MaxValue;
            manager.UpdateInstanceActivationDate(update);
            actual = manager.GetInstance(100, instance.InstanceId);
            Assert.Equal((Gazillion.LeaderboardState)1, actual.State);
            Assert.Equal(long.MaxValue, actual.ActivationDate);
        }

        [MySQLFact]
        public void UpdateOrInsertInstances_EmptyBatchDoesNotNeedAConnection()
        {
            MySQLLeaderboardDBManager manager = new();

            manager.UpdateOrInsertInstances([]);
        }

        [MySQLFact]
        public void GetInstances_HidesEmptyArchivesKeepsNewestAndRetainsRewardedArchives()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            DBLeaderboardInstance active = CreateInstance(10, 100);
            active.State = (Gazillion.LeaderboardState)1;
            DBLeaderboardInstance emptyArchive = CreateInstance(20, 100);
            emptyArchive.State = (Gazillion.LeaderboardState)2;
            DBLeaderboardInstance rewardedArchive = CreateInstance(21, 100);
            rewardedArchive.State = (Gazillion.LeaderboardState)2;
            DBLeaderboardInstance newestArchive = CreateInstance(22, 100);
            newestArchive.State = (Gazillion.LeaderboardState)2;
            manager.UpdateOrInsertInstances([active, emptyArchive, rewardedArchive, newestArchive]);
            manager.UpdateOrInsertEntries([CreateEntry(21, 200, 1, 2, [1]), CreateEntry(22, 201, 3, 4, [2])]);

            using (MySqlConnection connection = database.OpenConnection())
                ExecuteNonQuery(connection, "INSERT INTO leaderboard_reward (leaderboard_id, instance_id, participant_id, `rank`, reward_id, creation_date, rewarded_date) VALUES (100, 21, 200, 1, 1, 1, 1)");

            List<DBLeaderboardInstance> instances = manager.GetInstances(100, 1);

            Assert.Equal([10L, 22L], instances.Select(instance => instance.InstanceId));
            Assert.False(manager.GetInstance(100, 20).Visible);
            Assert.True(manager.GetInstance(100, 21).Visible);
            Assert.True(manager.GetInstance(100, 22).Visible);
        }

        [MySQLFact]
        public void GetInstances_VisibilityFailureRollsBackEarlierVisibilityChanges()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            DBLeaderboardInstance emptyArchive = CreateInstance(20, 100);
            emptyArchive.State = (Gazillion.LeaderboardState)2;
            DBLeaderboardInstance populatedArchive = CreateInstance(21, 100);
            populatedArchive.State = (Gazillion.LeaderboardState)2;
            DBLeaderboardInstance newestArchive = CreateInstance(22, 100);
            newestArchive.State = (Gazillion.LeaderboardState)2;
            manager.UpdateOrInsertInstances([emptyArchive, populatedArchive, newestArchive]);
            manager.UpdateOrInsertEntries([CreateEntry(21, 200, 1, 2, [1]), CreateEntry(22, 201, 3, 4, [2])]);

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_archive_visibility_update BEFORE UPDATE ON leaderboard_instance FOR EACH ROW
                        IF NEW.instance_id = 21 AND NEW.visible = 0 THEN
                            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced archive visibility failure';
                        END IF");

                Assert.Throws<MySqlException>(() => manager.GetInstances(100, 1));
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_archive_visibility_update");
            }

            Assert.True(manager.GetInstance(100, 20).Visible);
            Assert.True(manager.GetInstance(100, 21).Visible);
        }

        [MySQLFact]
        public void GetInstances_LocksArchiveRangeBeforeReconciliation()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager setupManager = Initialize(database);
            setupManager.InsertLeaderboards([CreateLeaderboard(100)]);
            DBLeaderboardInstance olderArchive = CreateInstance(19, 100);
            olderArchive.State = (Gazillion.LeaderboardState)2;
            DBLeaderboardInstance retainedArchive = CreateInstance(20, 100);
            retainedArchive.State = (Gazillion.LeaderboardState)2;
            setupManager.UpdateOrInsertInstances([olderArchive, retainedArchive]);
            setupManager.UpdateOrInsertEntries([CreateEntry(19, 200, 1, 2, [1]), CreateEntry(20, 201, 3, 4, [2])]);

            MySqlConnectionStringBuilder reconcilerConnectionStringBuilder = new(database.ConnectionString)
            {
                MaximumPoolSize = 1,
                ConnectionReset = false
            };
            string reconcilerConnectionString = reconcilerConnectionStringBuilder.ConnectionString;
            using (MySqlConnection connection = new(reconcilerConnectionString))
            {
                connection.Open();
                ExecuteNonQuery(connection, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED");
                Assert.Equal("READ-COMMITTED", GetSessionTransactionIsolation(connection));
            }

            string lockSuffix = Guid.NewGuid().ToString("N");
            string arrivedLock = $"get_instances_arrived_{lockSuffix}";
            string gateLock = $"get_instances_gate_{lockSuffix}";
            Task<List<DBLeaderboardInstance>> reconciliation = null;
            Task insertion = null;
            using ManualResetEventSlim insertionStarted = new();
            using MySqlConnection coordinator = database.OpenConnection();
            string isolationVariable = GetTransactionIsolationVariable(coordinator);
            try
            {
                MySQLLeaderboardDBManager manager = new(reconcilerConnectionString);
                MySQLLeaderboardDBManager concurrentManager = new(new MySqlConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ConnectionString);
                Assert.Equal(1, AcquireNamedLock(coordinator, gateLock));
                ExecuteNonQuery(coordinator, "CREATE TABLE archive_reconciliation_isolation (isolation_level VARCHAR(32) NOT NULL) ENGINE=InnoDB");
                ExecuteNonQuery(coordinator, $@"CREATE TRIGGER pause_archive_reconciliation BEFORE UPDATE ON leaderboard_instance FOR EACH ROW
                    BEGIN
                        DECLARE lock_result INT;
                        IF NEW.instance_id = 19 AND NEW.visible = 0 THEN
                            INSERT INTO archive_reconciliation_isolation (isolation_level) VALUES ({isolationVariable});
                            SELECT GET_LOCK('{arrivedLock}', 0) INTO lock_result;
                            SELECT GET_LOCK('{gateLock}', 10) INTO lock_result;
                        END IF;
                    END");

                reconciliation = Task.Run(() => manager.GetInstances(100, 1));
                Assert.True(SpinWait.SpinUntil(() => IsNamedLockHeld(coordinator, arrivedLock), TimeSpan.FromSeconds(10)));

                DBLeaderboardInstance newestArchive = CreateInstance(21, 100);
                newestArchive.State = (Gazillion.LeaderboardState)2;
                insertion = Task.Run(() =>
                {
                    insertionStarted.Set();
                    concurrentManager.InsertInstance(newestArchive);
                });
                Assert.True(insertionStarted.Wait(TimeSpan.FromSeconds(10)));
                Assert.False(insertion.Wait(TimeSpan.FromSeconds(1)));

                Assert.Equal(1, ReleaseNamedLock(coordinator, gateLock));
                reconciliation.GetAwaiter().GetResult();
                insertion.GetAwaiter().GetResult();

                Assert.True(manager.GetInstance(100, newestArchive.InstanceId).Visible);
                Assert.Equal("REPEATABLE-READ", Convert.ToString(ExecuteScalar(coordinator, "SELECT isolation_level FROM archive_reconciliation_isolation")));
                using MySqlConnection reusedConnection = new(reconcilerConnectionString);
                reusedConnection.Open();
                Assert.Equal("READ-COMMITTED", GetSessionTransactionIsolation(reusedConnection));
            }
            finally
            {
                Exception cleanupException = null;
                try
                {
                    try
                    {
                        ReleaseNamedLock(coordinator, gateLock);
                    }
                    finally
                    {
                        using MySqlConnection cleanupConnection = database.OpenConnection();
                        try
                        {
                            ExecuteNonQuery(cleanupConnection, "DROP TRIGGER IF EXISTS pause_archive_reconciliation");
                        }
                        finally
                        {
                            ExecuteNonQuery(cleanupConnection, "DROP TABLE IF EXISTS archive_reconciliation_isolation");
                        }
                    }
                }
                catch (Exception exception)
                {
                    cleanupException = exception;
                    throw;
                }
                finally
                {
                    try
                    {
                        MySqlConnection.ClearPool(new MySqlConnection(reconcilerConnectionString));
                    }
                    catch when (cleanupException != null)
                    {
                    }
                }
            }
        }

        [MySQLFact]
        public void UpdateActiveInstanceState_RejectsAbsentOrMismatchedTargetWithoutChanges()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard first = CreateLeaderboard(100);
            first.ActiveInstanceId = 10;
            DBLeaderboard second = CreateLeaderboard(101);
            manager.InsertLeaderboards([first, second]);
            DBLeaderboardInstance target = CreateInstance(20, 101);
            manager.InsertInstance(target);

            Assert.False(manager.UpdateActiveInstanceState(100, 999, 1));
            Assert.False(manager.UpdateActiveInstanceState(100, target.InstanceId, 1));

            Assert.Equal(10, manager.GetLeaderboards().Single(leaderboard => leaderboard.LeaderboardId == 100).ActiveInstanceId);
            Assert.Equal((Gazillion.LeaderboardState)0, manager.GetInstance(101, target.InstanceId).State);
        }

        [MySQLFact]
        public void UpdateActiveInstanceState_SecondUpdateFailureRollsBackInstanceState()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBLeaderboard leaderboard = CreateLeaderboard(100);
            leaderboard.ActiveInstanceId = 10;
            manager.InsertLeaderboards([leaderboard]);
            DBLeaderboardInstance target = CreateInstance(20, 100);
            manager.InsertInstance(target);

            try
            {
                using (MySqlConnection connection = database.OpenConnection())
                    ExecuteNonQuery(connection, @"CREATE TRIGGER fail_active_instance_change BEFORE UPDATE ON leaderboard FOR EACH ROW
                        IF NEW.leaderboard_id = 100 AND NEW.active_instance_id = 20 THEN
                            SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'induced active instance failure';
                        END IF");

                Assert.Throws<MySqlException>(() => manager.UpdateActiveInstanceState(100, target.InstanceId, 1));
            }
            finally
            {
                using MySqlConnection connection = database.OpenConnection();
                ExecuteNonQuery(connection, "DROP TRIGGER IF EXISTS fail_active_instance_change");
            }

            Assert.Equal(10, manager.GetLeaderboards().Single().ActiveInstanceId);
            Assert.Equal((Gazillion.LeaderboardState)0, manager.GetInstance(100, target.InstanceId).State);
        }

        [MySQLFact]
        public void EntryOperations_RoundTripSignedValuesBinaryStateAndHighScoreOrdering()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            manager.InsertInstance(CreateInstance(10, 100));
            DBLeaderboardEntry lowest = CreateEntry(10, long.MinValue + 1, long.MinValue + 2, long.MinValue + 3, [0, 255, 1]);
            DBLeaderboardEntry highest = CreateEntry(10, long.MaxValue, long.MaxValue - 1, long.MaxValue, [255, 0, 254]);

            manager.UpdateOrInsertEntries([highest, lowest]);

            Assert.Equal([lowest.ParticipantId, highest.ParticipantId], manager.GetEntries(10, true).Select(entry => entry.ParticipantId));
            Assert.Equal([highest.ParticipantId, lowest.ParticipantId], manager.GetEntries(10, false).Select(entry => entry.ParticipantId));
            DBLeaderboardEntry actual = manager.GetEntries(10, true).Single(entry => entry.ParticipantId == lowest.ParticipantId);
            Assert.Equal(lowest.Score, actual.Score);
            Assert.Equal(lowest.HighScore, actual.HighScore);
            Assert.Equal(lowest.RuleStates, actual.RuleStates);

            lowest.Score = 7;
            lowest.HighScore = 8;
            lowest.RuleStates = [9, 0, 8];
            manager.UpdateOrInsertEntries([lowest]);
            actual = manager.GetEntries(10, false).Single(entry => entry.ParticipantId == lowest.ParticipantId);
            Assert.Equal(7, actual.Score);
            Assert.Equal(8, actual.HighScore);
            Assert.Equal([9, 0, 8], actual.RuleStates);
        }

        [MySQLFact]
        public void UpdateOrInsertEntries_EmptyBatchDoesNotNeedAConnection()
        {
            MySQLLeaderboardDBManager manager = new();

            manager.UpdateOrInsertEntries([]);
        }

        [MySQLFact]
        public void MetaEntries_UseCompositeIdentityPermitMissingInstancesAndLookUpExactTriple()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            DBMetaEntry first = new()
            {
                LeaderboardId = 100,
                InstanceId = long.MinValue,
                SubLeaderboardId = long.MinValue + 1,
                SubInstanceId = long.MaxValue
            };
            DBMetaEntry second = new()
            {
                LeaderboardId = 100,
                InstanceId = long.MinValue,
                SubLeaderboardId = long.MaxValue,
                SubInstanceId = long.MinValue + 2
            };

            manager.InsertMetaEntries([first, second]);

            Assert.Equal(first.SubInstanceId, manager.GetSubInstanceId(first.LeaderboardId, first.InstanceId, first.SubLeaderboardId));
            Assert.Equal(second.SubInstanceId, manager.GetSubInstanceId(second.LeaderboardId, second.InstanceId, second.SubLeaderboardId));
            Assert.Equal(0, manager.GetSubInstanceId(first.LeaderboardId, first.InstanceId, 0));
            Assert.Equal([first.SubLeaderboardId, second.SubLeaderboardId], manager.GetMetaEntries(100, long.MinValue).Select(entry => entry.SubLeaderboardId).Order());
            Assert.Throws<MySqlException>(() => manager.InsertMetaEntries([first]));
        }

        [MySQLFact]
        public void MetaEntries_RequireLeaderboardAndCascadeWhenLeaderboardIsDeleted()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            DBMetaEntry missingParent = CreateMetaEntry(100, 10);

            Assert.Throws<MySqlException>(() => manager.InsertMetaEntries([missingParent]));

            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            manager.InsertMetaEntries([missingParent]);
            using MySqlConnection connection = database.OpenConnection();
            ExecuteNonQuery(connection, "DELETE FROM leaderboard WHERE leaderboard_id = 100");

            Assert.Equal(0, GetRowCount(connection, "leaderboard_meta_entry"));
        }

        [MySQLFact]
        public void Rewards_UseCompositeIdentityAndLeaveInsertedRewardsPending()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            manager.InsertInstance(CreateInstance(10, 100));
            DBRewardEntry first = CreateReward(100, 10, long.MinValue, long.MaxValue, int.MinValue, long.MinValue);
            DBRewardEntry second = CreateReward(100, 10, long.MaxValue, long.MinValue, int.MaxValue, long.MaxValue);

            manager.InsertRewards([first, second]);

            List<DBRewardEntry> rewards = manager.GetRewards(long.MinValue);
            DBRewardEntry actual = Assert.Single(rewards);
            Assert.Equal(first.LeaderboardId, actual.LeaderboardId);
            Assert.Equal(first.InstanceId, actual.InstanceId);
            Assert.Equal(first.RewardId, actual.RewardId);
            Assert.Equal(first.Rank, actual.Rank);
            Assert.Equal(first.CreationDate, actual.CreationDate);
            Assert.Equal(0, actual.RewardedDate);
            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(1, Convert.ToInt32(ExecuteScalar(connection, "SELECT rewarded_date IS NULL FROM leaderboard_reward WHERE leaderboard_id = 100 AND instance_id = 10 AND participant_id = @participantId", ("@participantId", first.ParticipantId))));
            Assert.Throws<MySqlException>(() => manager.InsertRewards([first]));
        }

        [MySQLFact]
        public void Rewards_RequireInstancesButNotLeaderboards_FinalizeOnlyExactRewardAndCascade()
        {
            using MySQLTestDatabase database = new();
            MySQLLeaderboardDBManager manager = Initialize(database);
            manager.InsertLeaderboards([CreateLeaderboard(100)]);
            manager.InsertInstance(CreateInstance(10, 100));
            manager.InsertInstance(CreateInstance(11, 100));
            DBRewardEntry target = CreateReward(100, 10, 50, 1, 1, 10);
            DBRewardEntry differentLeaderboard = CreateReward(101, 10, 50, 1, 2, 20);
            DBRewardEntry differentInstance = CreateReward(100, 11, 50, 1, 3, 30);

            manager.InsertRewards([target, differentLeaderboard, differentInstance]);
            Assert.Throws<MySqlException>(() => manager.InsertRewards([CreateReward(100, 99, 51, 2, 4, 40)]));

            target.RewardId = 99;
            target.Rank = 99;
            target.CreationDate = 99;
            target.RewardedDate = long.MaxValue;
            manager.UpdateReward(target);

            Assert.Equal([differentLeaderboard.InstanceId, differentInstance.InstanceId], manager.GetRewards(50).Select(reward => reward.InstanceId).Order());
            using MySqlConnection connection = database.OpenConnection();
            Assert.Equal(1, Convert.ToInt32(ExecuteScalar(connection, "SELECT rewarded_date = @rewardedDate AND reward_id = 1 AND `rank` = 1 AND creation_date = 10 FROM leaderboard_reward WHERE leaderboard_id = 100 AND instance_id = 10 AND participant_id = 50", ("@rewardedDate", target.RewardedDate))));
            ExecuteNonQuery(connection, "DELETE FROM leaderboard_instance WHERE instance_id = 10");
            Assert.Equal(1, GetRowCount(connection, "leaderboard_reward"));
            Assert.Equal([11L], manager.GetRewards(50).Select(reward => reward.InstanceId));
        }

        private static MySQLLeaderboardDBManager Initialize(MySQLTestDatabase database)
        {
            MySQLLeaderboardDBManager manager = new(database.ConnectionString);
            Assert.True(manager.Initialize(out _));
            return manager;
        }

        private static DBLeaderboard CreateLeaderboard(long leaderboardId) => new()
        {
            LeaderboardId = leaderboardId,
            PrototypeName = $"Prototype{leaderboardId}",
            ActiveInstanceId = leaderboardId + 1,
            IsEnabled = true,
            StartTime = leaderboardId + 2,
            MaxResetCount = checked((int)leaderboardId + 3)
        };

        private static DBLeaderboardInstance CreateInstance(long instanceId, long leaderboardId) => new()
        {
            InstanceId = instanceId,
            LeaderboardId = leaderboardId,
            ActivationDate = instanceId + 1,
            Visible = true
        };

        private static DBLeaderboardEntry CreateEntry(long instanceId, long participantId, long score, long highScore, byte[] ruleStates) => new()
        {
            InstanceId = instanceId,
            ParticipantId = participantId,
            Score = score,
            HighScore = highScore,
            RuleStates = ruleStates
        };

        private static DBRewardEntry CreateReward(long leaderboardId, long instanceId, long participantId, long rewardId, int rank, long creationDate) => new()
        {
            LeaderboardId = leaderboardId,
            InstanceId = instanceId,
            ParticipantId = participantId,
            RewardId = rewardId,
            Rank = rank,
            CreationDate = creationDate,
            RewardedDate = 1
        };

        private static DBMetaEntry CreateMetaEntry(long leaderboardId, long instanceId) => new()
        {
            LeaderboardId = leaderboardId,
            InstanceId = instanceId,
            SubLeaderboardId = leaderboardId + 1,
            SubInstanceId = instanceId + 1
        };

        private static void AssertLeaderboard(DBLeaderboard expected, DBLeaderboard actual)
        {
            Assert.Equal(expected.LeaderboardId, actual.LeaderboardId);
            Assert.Equal(expected.PrototypeName, actual.PrototypeName);
            Assert.Equal(expected.ActiveInstanceId, actual.ActiveInstanceId);
            Assert.Equal(expected.IsEnabled, actual.IsEnabled);
            Assert.Equal(expected.StartTime, actual.StartTime);
            Assert.Equal(expected.MaxResetCount, actual.MaxResetCount);
        }

        private static int GetRowCount(MySqlConnection connection, string tableName) => Convert.ToInt32(ExecuteScalar(connection, $"SELECT COUNT(*) FROM {tableName}"));

        private static object ExecuteScalar(MySqlConnection connection, string commandText)
        {
            using MySqlCommand command = new(commandText, connection);
            return command.ExecuteScalar();
        }

        private static object ExecuteScalar(MySqlConnection connection, string commandText, params (string Name, object Value)[] parameters)
        {
            using MySqlCommand command = new(commandText, connection);
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar();
        }

        private static int AcquireNamedLock(MySqlConnection connection, string name)
        {
            using MySqlCommand command = new("SELECT GET_LOCK(@name, 0)", connection);
            command.Parameters.AddWithValue("@name", name);
            return Convert.ToInt32(command.ExecuteScalar());
        }

        private static bool IsNamedLockHeld(MySqlConnection connection, string name)
        {
            using MySqlCommand command = new("SELECT IS_USED_LOCK(@name)", connection);
            command.Parameters.AddWithValue("@name", name);
            return command.ExecuteScalar() is not null and not DBNull;
        }

        private static int ReleaseNamedLock(MySqlConnection connection, string name)
        {
            using MySqlCommand command = new("SELECT RELEASE_LOCK(@name)", connection);
            command.Parameters.AddWithValue("@name", name);
            object result = command.ExecuteScalar();
            return result is DBNull ? 0 : Convert.ToInt32(result);
        }

        private static string GetSessionTransactionIsolation(MySqlConnection connection)
        {
            return Convert.ToString(ExecuteScalar(connection, $"SELECT {GetTransactionIsolationVariable(connection)}"));
        }

        private static string GetTransactionIsolationVariable(MySqlConnection connection)
        {
            string version = Convert.ToString(ExecuteScalar(connection, "SELECT VERSION()"));
            return version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "@@tx_isolation" : "@@transaction_isolation";
        }

        private static void ExecuteNonQuery(MySqlConnection connection, string commandText)
        {
            using MySqlCommand command = new(commandText, connection);
            command.ExecuteNonQuery();
        }
    }
}
