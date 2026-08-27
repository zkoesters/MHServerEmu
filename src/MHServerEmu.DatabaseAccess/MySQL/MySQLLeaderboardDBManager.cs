using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    public class MySQLLeaderboardDBManager : ILeaderboardDBManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private MySQLConnectionInfo _connectionInfo;

        public MySQLLeaderboardDBManager()
        {
        }

        internal MySQLLeaderboardDBManager(string connectionString)
        {
            if (MySQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out string error) == false)
                throw new ArgumentException(error, nameof(connectionString));
        }

        public bool Initialize(out bool isNewDatabase)
        {
            isNewDatabase = false;
            if (TryLoadConnectionInfo() == false)
                return false;

            try
            {
                using MySqlConnection connection = GetConnection();
                MySQLLeaderboardSchemaResult result = MySQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
                isNewDatabase = result.Created;
                Logger.Info($"Using MySQL leaderboard database {_connectionInfo.Description}");
                return true;
            }
            catch (Exception exception)
            {
                Logger.Error(CreateInitializationErrorLogMessage(exception));
                return false;
            }
        }

        public void InsertInitialData(List<DBLeaderboard> dbLeaderboards, List<DBLeaderboardInstance> dbInstances, List<DBMetaEntry> dbMetaEntries)
        {
            if (dbLeaderboards.Count == 0 && dbInstances.Count == 0 && dbMetaEntries.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                InsertLeaderboards(connection, transaction, dbLeaderboards);
                InsertInstances(connection, transaction, dbInstances);
                InsertMetaEntries(connection, transaction, dbMetaEntries);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public void InsertLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                InsertLeaderboards(connection, transaction, dbLeaderboards);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public void UpdateLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(@"
                    UPDATE leaderboard
                    SET prototype_name = @PrototypeName,
                        active_instance_id = @ActiveInstanceId,
                        is_enabled = @IsEnabled,
                        start_time = @StartTime,
                        max_reset_count = @MaxResetCount
                    WHERE leaderboard_id = @LeaderboardId", dbLeaderboards, transaction);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public DBLeaderboard[] GetLeaderboards()
        {
            using MySqlConnection connection = GetConnection();
            return connection.Query<DBLeaderboard>(@"
                SELECT leaderboard_id AS LeaderboardId,
                       prototype_name AS PrototypeName,
                       active_instance_id AS ActiveInstanceId,
                       is_enabled AS IsEnabled,
                       start_time AS StartTime,
                       max_reset_count AS MaxResetCount
                FROM leaderboard").ToArray();
        }

        public bool UpdateActiveInstanceState(long leaderboardId, long activeInstanceId, int state)
        {
            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                bool targetExists = connection.Query<long>(@"
                    SELECT instance_id
                    FROM leaderboard_instance
                    WHERE instance_id = @InstanceId AND leaderboard_id = @LeaderboardId
                    FOR UPDATE", new { InstanceId = activeInstanceId, LeaderboardId = leaderboardId }, transaction).Any();
                if (targetExists == false)
                {
                    transaction.Rollback();
                    return false;
                }

                connection.Execute(@"
                    UPDATE leaderboard_instance
                    SET state = @State
                    WHERE instance_id = @InstanceId AND leaderboard_id = @LeaderboardId",
                    new { InstanceId = activeInstanceId, LeaderboardId = leaderboardId, State = state }, transaction);
                connection.Execute(@"
                    UPDATE leaderboard
                    SET active_instance_id = @InstanceId
                    WHERE leaderboard_id = @LeaderboardId",
                    new { InstanceId = activeInstanceId, LeaderboardId = leaderboardId }, transaction);
                transaction.Commit();
                return true;
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public List<DBLeaderboardInstance> GetInstances(long leaderboardId, int maxArchivedInstances)
        {
            int archivedLimit = Math.Max(0, maxArchivedInstances);
            using MySqlConnection connection = GetConnection();
            string sessionIsolation = GetSessionTransactionIsolation(connection);
            bool reconciliationFailed = false;
            try
            {
                using MySqlTransaction transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
                try
                {
                    // Repeatable read makes this range lock exclude concurrent archive inserts.
                    connection.Query<long>(@"
                        SELECT instance_id
                        FROM leaderboard_instance FORCE INDEX (idx_instances_leaderboardid)
                        WHERE leaderboard_id = @LeaderboardId
                        FOR UPDATE", new { LeaderboardId = leaderboardId }, transaction).ToList();

                    connection.Execute(@"
                        UPDATE leaderboard_instance
                        SET visible = FALSE
                        WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                          AND NOT EXISTS (
                              SELECT 1
                              FROM leaderboard_entry
                              WHERE leaderboard_entry.instance_id = leaderboard_instance.instance_id
                          )", new { LeaderboardId = leaderboardId }, transaction);

                    List<long> recentArchivedInstanceIds = connection.Query<long>(@"
                        SELECT instance_id
                        FROM leaderboard_instance
                        WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                        ORDER BY instance_id DESC
                        LIMIT @ArchivedLimit", new { LeaderboardId = leaderboardId, ArchivedLimit = archivedLimit }, transaction).ToList();

                    const string hideArchivedInstances = @"
                        UPDATE leaderboard_instance
                        SET visible = FALSE
                        WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                          AND NOT EXISTS (
                              SELECT 1
                              FROM leaderboard_reward
                              WHERE leaderboard_reward.leaderboard_id = leaderboard_instance.leaderboard_id
                                AND leaderboard_reward.instance_id = leaderboard_instance.instance_id
                                AND leaderboard_reward.rewarded_date IS NOT NULL
                          )";
                    if (recentArchivedInstanceIds.Count == 0)
                        connection.Execute(hideArchivedInstances, new { LeaderboardId = leaderboardId }, transaction);
                    else
                        connection.Execute(hideArchivedInstances + " AND instance_id NOT IN @RecentArchivedInstanceIds",
                            new { LeaderboardId = leaderboardId, RecentArchivedInstanceIds = recentArchivedInstanceIds }, transaction);

                    List<DBLeaderboardInstance> instances = connection.Query<DBLeaderboardInstance>(@"
                        SELECT instance_id AS InstanceId,
                               leaderboard_id AS LeaderboardId,
                               state AS State,
                               activation_date AS ActivationDate,
                               visible AS Visible
                        FROM leaderboard_instance
                        WHERE leaderboard_id = @LeaderboardId AND state <= 1
                        ORDER BY instance_id DESC", new { LeaderboardId = leaderboardId }, transaction).ToList();
                    instances.AddRange(connection.Query<DBLeaderboardInstance>(@"
                        SELECT instance_id AS InstanceId,
                               leaderboard_id AS LeaderboardId,
                               state AS State,
                               activation_date AS ActivationDate,
                               visible AS Visible
                        FROM leaderboard_instance
                        WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                        ORDER BY instance_id DESC
                        LIMIT @ArchivedLimit", new { LeaderboardId = leaderboardId, ArchivedLimit = archivedLimit }, transaction));
                    transaction.Commit();
                    return instances;
                }
                catch
                {
                    TryRollback(transaction);
                    throw;
                }
            }
            catch
            {
                reconciliationFailed = true;
                throw;
            }
            finally
            {
                try
                {
                    SetSessionTransactionIsolation(connection, sessionIsolation);
                }
                catch when (reconciliationFailed)
                {
                }
            }
        }

        public DBLeaderboardInstance GetInstance(long leaderboardId, long instanceId)
        {
            using MySqlConnection connection = GetConnection();
            return connection.QueryFirstOrDefault<DBLeaderboardInstance>(@"
                SELECT instance_id AS InstanceId,
                       leaderboard_id AS LeaderboardId,
                       state AS State,
                       activation_date AS ActivationDate,
                       visible AS Visible
                FROM leaderboard_instance
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId });
        }

        public void UpdateOrInsertInstances(List<DBLeaderboardInstance> dbInstances)
        {
            if (dbInstances.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(@"
                    INSERT INTO leaderboard_instance
                        (instance_id, leaderboard_id, state, activation_date, visible)
                    VALUES
                        (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)
                    ON DUPLICATE KEY UPDATE
                        state = VALUES(state),
                        activation_date = VALUES(activation_date),
                        visible = VALUES(visible)", dbInstances, transaction);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public void InsertInstance(DBLeaderboardInstance dbInstance)
        {
            using MySqlConnection connection = GetConnection();
            connection.Execute(@"
                INSERT INTO leaderboard_instance
                    (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES
                    (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)", dbInstance);
        }

        public void UpdateInstanceState(long instanceId, int state)
        {
            using MySqlConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE leaderboard_instance
                SET state = @State
                WHERE instance_id = @InstanceId", new { InstanceId = instanceId, State = state });
        }

        public void UpdateInstanceActivationDate(DBLeaderboardInstance dbInstance)
        {
            using MySqlConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE leaderboard_instance
                SET activation_date = @ActivationDate
                WHERE instance_id = @InstanceId", dbInstance);
        }

        public List<DBLeaderboardEntry> GetEntries(long instanceId, bool ascending)
        {
            using MySqlConnection connection = GetConnection();
            string order = ascending ? "ASC" : "DESC";
            return connection.Query<DBLeaderboardEntry>(@"
                SELECT instance_id AS InstanceId,
                       participant_id AS ParticipantId,
                       score AS Score,
                       high_score AS HighScore,
                       rule_states AS RuleStates
                FROM leaderboard_entry
                WHERE instance_id = @InstanceId
                ORDER BY high_score " + order, new { InstanceId = instanceId }).ToList();
        }

        public void UpdateOrInsertEntries(List<DBLeaderboardEntry> dbEntries)
        {
            if (dbEntries.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(@"
                    INSERT INTO leaderboard_entry
                        (instance_id, participant_id, score, high_score, rule_states)
                    VALUES
                        (@InstanceId, @ParticipantId, @Score, @HighScore, @RuleStates)
                    ON DUPLICATE KEY UPDATE
                        score = VALUES(score),
                        high_score = VALUES(high_score),
                        rule_states = VALUES(rule_states)", dbEntries, transaction);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public long GetSubInstanceId(long leaderboardId, long instanceId, long subLeaderboardId)
        {
            using MySqlConnection connection = GetConnection();
            return connection.QuerySingleOrDefault<long>(@"
                SELECT sub_instance_id
                FROM leaderboard_meta_entry
                WHERE leaderboard_id = @LeaderboardId
                  AND instance_id = @InstanceId
                  AND sub_leaderboard_id = @SubLeaderboardId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId, SubLeaderboardId = subLeaderboardId });
        }

        public void InsertMetaEntries(List<DBMetaEntry> instances)
        {
            if (instances.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                InsertMetaEntries(connection, transaction, instances);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public List<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId)
        {
            using MySqlConnection connection = GetConnection();
            return connection.Query<DBMetaEntry>(@"
                SELECT leaderboard_id AS LeaderboardId,
                       instance_id AS InstanceId,
                       sub_leaderboard_id AS SubLeaderboardId,
                       sub_instance_id AS SubInstanceId
                FROM leaderboard_meta_entry
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId }).ToList();
        }

        public void InsertRewards(List<DBRewardEntry> dbRewards)
        {
            if (dbRewards.Count == 0)
                return;

            using MySqlConnection connection = GetConnection();
            using MySqlTransaction transaction = connection.BeginTransaction();
            try
            {
                connection.Execute(@"
                    INSERT INTO leaderboard_reward
                        (leaderboard_id, instance_id, participant_id, `rank`, reward_id, creation_date)
                    VALUES
                        (@LeaderboardId, @InstanceId, @ParticipantId, @Rank, @RewardId, @CreationDate)", dbRewards, transaction);
                transaction.Commit();
            }
            catch
            {
                TryRollback(transaction);
                throw;
            }
        }

        public List<DBRewardEntry> GetRewards(long participantId)
        {
            using MySqlConnection connection = GetConnection();
            return connection.Query<DBRewardEntry>(@"
                SELECT leaderboard_id AS LeaderboardId,
                       instance_id AS InstanceId,
                       participant_id AS ParticipantId,
                       `rank` AS `Rank`,
                       reward_id AS RewardId,
                       creation_date AS CreationDate,
                       rewarded_date AS RewardedDate
                FROM leaderboard_reward
                WHERE participant_id = @ParticipantId AND rewarded_date IS NULL",
                new { ParticipantId = participantId }).ToList();
        }

        public void UpdateReward(DBRewardEntry reward)
        {
            using MySqlConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE leaderboard_reward
                SET rewarded_date = @RewardedDate
                WHERE leaderboard_id = @LeaderboardId
                  AND instance_id = @InstanceId
                  AND participant_id = @ParticipantId", reward);
        }

        internal string CreateInitializationErrorLogMessage(Exception exception)
        {
            return CreateInitializationErrorLogMessage(exception is MySqlException mySqlException ? mySqlException.Number : null);
        }

        internal string CreateInitializationErrorLogMessage(int? errorNumber)
        {
            string category = errorNumber switch
            {
                1042 or 2002 or 2003 or 2006 or 2013 => "connection failure",
                1045 => "authentication failure",
                1062 => "schema uniqueness failure",
                null => "unexpected failure",
                _ => "schema or migration failure"
            };
            string message = $"Initialize(): MySQL leaderboard error for database {_connectionInfo?.Description ?? "unconfigured"}: {category}";
            return errorNumber.HasValue ? $"{message} (error {errorNumber.Value})" : message;
        }

        private bool TryLoadConnectionInfo()
        {
            if (_connectionInfo != null)
                return true;

            string connectionString = ConfigManager.Instance.GetConfig<MySQLDBManagerConfig>().ConnectionString;
            if (MySQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out _))
                return true;

            Logger.Error("Initialize(): MySQL leaderboard configuration is invalid");
            return false;
        }

        private MySqlConnection GetConnection()
        {
            MySqlConnection connection = new(_connectionInfo.ConnectionString);
            connection.Open();
            return connection;
        }

        private static void InsertLeaderboards(MySqlConnection connection, MySqlTransaction transaction, List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard
                    (leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count)
                VALUES
                    (@LeaderboardId, @PrototypeName, @ActiveInstanceId, @IsEnabled, @StartTime, @MaxResetCount)", dbLeaderboards, transaction);
        }

        private static void InsertInstances(MySqlConnection connection, MySqlTransaction transaction, List<DBLeaderboardInstance> dbInstances)
        {
            if (dbInstances.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard_instance
                    (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES
                    (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)", dbInstances, transaction);
        }

        private static void InsertMetaEntries(MySqlConnection connection, MySqlTransaction transaction, List<DBMetaEntry> dbMetaEntries)
        {
            if (dbMetaEntries.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard_meta_entry
                    (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id)
                VALUES
                    (@LeaderboardId, @InstanceId, @SubLeaderboardId, @SubInstanceId)", dbMetaEntries, transaction);
        }

        private static string GetSessionTransactionIsolation(MySqlConnection connection)
        {
            string version = connection.ExecuteScalar<string>("SELECT VERSION()");
            string isolationVariable = version.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "@@tx_isolation" : "@@transaction_isolation";
            return connection.ExecuteScalar<string>($"SELECT {isolationVariable}");
        }

        private static void SetSessionTransactionIsolation(MySqlConnection connection, string isolation)
        {
            string commandText = isolation.ToUpperInvariant() switch
            {
                "READ-UNCOMMITTED" => "SET SESSION TRANSACTION ISOLATION LEVEL READ UNCOMMITTED",
                "READ-COMMITTED" => "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED",
                "REPEATABLE-READ" => "SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ",
                "SERIALIZABLE" => "SET SESSION TRANSACTION ISOLATION LEVEL SERIALIZABLE",
                _ => throw new InvalidOperationException($"Unsupported transaction isolation level: {isolation}")
            };
            connection.Execute(commandText);
        }

        private static void TryRollback(MySqlTransaction transaction)
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
            }
        }
    }
}
