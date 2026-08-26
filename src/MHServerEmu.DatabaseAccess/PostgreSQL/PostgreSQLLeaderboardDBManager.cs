using Dapper;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public class PostgreSQLLeaderboardDBManager : ILeaderboardDBManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private PostgreSQLConnectionInfo _connectionInfo;

        public PostgreSQLLeaderboardDBManager() { }

        internal PostgreSQLLeaderboardDBManager(string connectionString)
        {
            if (PostgreSQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out string error) == false)
                throw new ArgumentException(error, nameof(connectionString));
        }

        public bool Initialize(out bool isNewDatabase)
        {
            isNewDatabase = false;

            try
            {
                if (TryLoadConnectionInfo() == false)
                {
                    LogInitializationFailure(new ArgumentException());
                    return false;
                }

                using NpgsqlConnection connection = GetConnection();
                PostgreSQLLeaderboardSchemaResult result = PostgreSQLLeaderboardSchemaManager.EnsureCurrentSchema(connection);
                isNewDatabase = result.Created;
                return true;
            }
            catch (Exception exception)
            {
                LogInitializationFailure(exception);
                return false;
            }
        }

        public void InsertInitialData(List<DBLeaderboard> dbLeaderboards, List<DBLeaderboardInstance> dbInstances, List<DBMetaEntry> dbMetaEntries)
        {
            if (dbLeaderboards.Count == 0 && dbInstances.Count == 0 && dbMetaEntries.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            try
            {
                InsertLeaderboards(connection, transaction, dbLeaderboards);
                InsertInstances(connection, transaction, dbInstances);
                InsertMetaEntries(connection, transaction, dbMetaEntries);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        public void InsertLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            InsertLeaderboards(connection, transaction, dbLeaderboards);
            transaction.Commit();
        }

        private static void InsertLeaderboards(NpgsqlConnection connection, NpgsqlTransaction transaction, List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard
                    (leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count)
                VALUES
                    (@LeaderboardId, @PrototypeName, @ActiveInstanceId, @IsEnabled, @StartTime, @MaxResetCount)",
                dbLeaderboards, transaction);
        }

        public void UpdateLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            connection.Execute(@"
                UPDATE leaderboard
                SET active_instance_id = @ActiveInstanceId,
                    is_enabled = @IsEnabled,
                    start_time = @StartTime,
                    max_reset_count = @MaxResetCount
                WHERE leaderboard_id = @LeaderboardId", dbLeaderboards, transaction);
            transaction.Commit();
        }

        public DBLeaderboard[] GetLeaderboards()
        {
            using NpgsqlConnection connection = GetConnection();
            return connection.Query<DBLeaderboard>(@"
                SELECT leaderboard_id AS ""LeaderboardId"", prototype_name AS ""PrototypeName"",
                    active_instance_id AS ""ActiveInstanceId"", is_enabled AS ""IsEnabled"",
                    start_time AS ""StartTime"", max_reset_count AS ""MaxResetCount""
                FROM leaderboard").ToArray();
        }

        public bool UpdateActiveInstanceState(long leaderboardId, long activeInstanceId, int state)
        {
            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            int rows = connection.Execute(@"
                UPDATE leaderboard SET active_instance_id = @ActiveInstanceId
                WHERE leaderboard_id = @LeaderboardId",
                new { LeaderboardId = leaderboardId, ActiveInstanceId = activeInstanceId }, transaction);
            rows += DoUpdateInstanceState(connection, activeInstanceId, state, transaction);

            if (rows != 2)
                return false;

            transaction.Commit();
            return true;
        }

        public List<DBLeaderboardInstance> GetInstances(long leaderboardId, int maxArchivedInstances)
        {
            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            List<DBLeaderboardInstance> instances = new(connection.Query<DBLeaderboardInstance>(@"
                SELECT instance_id AS ""InstanceId"", leaderboard_id AS ""LeaderboardId"", state AS ""State"",
                    activation_date AS ""ActivationDate"", visible AS ""Visible""
                FROM leaderboard_instance
                WHERE leaderboard_id = @LeaderboardId AND state <= 1
                ORDER BY instance_id DESC", new { LeaderboardId = leaderboardId }, transaction));

            UpdateArchivedInstanceVisibility(connection, transaction, leaderboardId, maxArchivedInstances);
            instances.AddRange(connection.Query<DBLeaderboardInstance>(@"
                SELECT instance_id AS ""InstanceId"", leaderboard_id AS ""LeaderboardId"", state AS ""State"",
                    activation_date AS ""ActivationDate"", visible AS ""Visible""
                FROM leaderboard_instance
                WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                ORDER BY instance_id DESC
                LIMIT @MaxArchivedInstances", new { LeaderboardId = leaderboardId, MaxArchivedInstances = maxArchivedInstances }, transaction));
            transaction.Commit();
            return instances;
        }

        public DBLeaderboardInstance GetInstance(long leaderboardId, long instanceId)
        {
            using NpgsqlConnection connection = GetConnection();
            return connection.QueryFirstOrDefault<DBLeaderboardInstance>(@"
                SELECT instance_id AS ""InstanceId"", leaderboard_id AS ""LeaderboardId"", state AS ""State"",
                    activation_date AS ""ActivationDate"", visible AS ""Visible""
                FROM leaderboard_instance
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId });
        }

        private static void UpdateArchivedInstanceVisibility(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, int maxArchivedInstances)
        {
            connection.Execute(@"
                UPDATE leaderboard_instance
                SET visible = FALSE
                WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                    AND NOT EXISTS (
                        SELECT 1 FROM leaderboard_entry
                        WHERE leaderboard_entry.instance_id = leaderboard_instance.instance_id
                    )", new { LeaderboardId = leaderboardId }, transaction);

            List<long> excludedInstanceIds = connection.Query<long>(@"
                SELECT instance_id
                FROM leaderboard_instance
                WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                ORDER BY instance_id DESC
                LIMIT @MaxArchivedInstances", new { LeaderboardId = leaderboardId, MaxArchivedInstances = maxArchivedInstances }, transaction).ToList();
            if (excludedInstanceIds.Count == 0)
                excludedInstanceIds.Add(0);

            connection.Execute(@"
                UPDATE leaderboard_instance
                SET visible = FALSE
                WHERE leaderboard_id = @LeaderboardId AND state > 1 AND visible = TRUE
                    AND instance_id <> ALL(@ExcludedInstanceIds)
                    AND NOT EXISTS (
                        SELECT 1 FROM leaderboard_reward
                        WHERE leaderboard_reward.leaderboard_id = leaderboard_instance.leaderboard_id
                            AND leaderboard_reward.instance_id = leaderboard_instance.instance_id
                            AND leaderboard_reward.rewarded_date IS NOT NULL
                    )", new { LeaderboardId = leaderboardId, ExcludedInstanceIds = excludedInstanceIds.ToArray() }, transaction);
        }

        public void UpdateOrInsertInstances(List<DBLeaderboardInstance> dbInstances)
        {
            if (dbInstances.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            InsertOrUpdateInstances(connection, transaction, dbInstances);
            transaction.Commit();
        }

        private static void InsertInstances(NpgsqlConnection connection, NpgsqlTransaction transaction, List<DBLeaderboardInstance> dbInstances)
        {
            if (dbInstances.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard_instance
                    (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES
                    (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)",
                dbInstances.Select(CreateInstanceParameters), transaction);
        }

        private static void InsertOrUpdateInstances(NpgsqlConnection connection, NpgsqlTransaction transaction, List<DBLeaderboardInstance> dbInstances)
        {
            const string updateCommand = @"
                UPDATE leaderboard_instance
                SET state = @State, activation_date = @ActivationDate, visible = @Visible
                WHERE instance_id = @InstanceId";
            const string insertCommand = @"
                INSERT INTO leaderboard_instance
                    (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES
                    (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)";

            foreach (DBLeaderboardInstance instance in dbInstances)
            {
                object parameters = CreateInstanceParameters(instance);
                if (connection.Execute(updateCommand, parameters, transaction) == 0)
                    connection.Execute(insertCommand, parameters, transaction);
            }
        }

        public void InsertInstance(DBLeaderboardInstance dbInstance)
        {
            using NpgsqlConnection connection = GetConnection();
            connection.Execute(@"
                INSERT INTO leaderboard_instance
                    (instance_id, leaderboard_id, state, activation_date, visible)
                VALUES
                    (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)", CreateInstanceParameters(dbInstance));
        }

        public void UpdateInstanceState(long instanceId, int state)
        {
            using NpgsqlConnection connection = GetConnection();
            DoUpdateInstanceState(connection, instanceId, state, null);
        }

        private static int DoUpdateInstanceState(NpgsqlConnection connection, long instanceId, int state, NpgsqlTransaction transaction)
        {
            return connection.Execute(@"
                UPDATE leaderboard_instance SET state = @State
                WHERE instance_id = @InstanceId", new { InstanceId = instanceId, State = state }, transaction);
        }

        private static object CreateInstanceParameters(DBLeaderboardInstance instance)
        {
            return new
            {
                instance.InstanceId,
                instance.LeaderboardId,
                State = (int)instance.State,
                instance.ActivationDate,
                instance.Visible
            };
        }

        public void UpdateInstanceActivationDate(DBLeaderboardInstance dbInstance)
        {
            using NpgsqlConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE leaderboard_instance SET activation_date = @ActivationDate
                WHERE instance_id = @InstanceId", dbInstance);
        }

        public List<DBLeaderboardEntry> GetEntries(long instanceId, bool ascending)
        {
            using NpgsqlConnection connection = GetConnection();
            string order = ascending ? "ASC" : "DESC";
            return connection.Query<DBLeaderboardEntry>($@"
                SELECT instance_id AS ""InstanceId"", participant_id AS ""ParticipantId"", score AS ""Score"",
                    high_score AS ""HighScore"", rule_states AS ""RuleStates""
                FROM leaderboard_entry
                WHERE instance_id = @InstanceId
                ORDER BY high_score {order}", new { InstanceId = instanceId }).ToList();
        }

        public void UpdateOrInsertEntries(List<DBLeaderboardEntry> dbEntries)
        {
            if (dbEntries.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            const string updateCommand = @"
                UPDATE leaderboard_entry
                SET score = @Score, high_score = @HighScore, rule_states = @RuleStates
                WHERE instance_id = @InstanceId AND participant_id = @ParticipantId";
            const string insertCommand = @"
                INSERT INTO leaderboard_entry
                    (instance_id, participant_id, score, high_score, rule_states)
                VALUES
                    (@InstanceId, @ParticipantId, @Score, @HighScore, @RuleStates)";

            foreach (DBLeaderboardEntry entry in dbEntries)
                if (connection.Execute(updateCommand, entry, transaction) == 0)
                    connection.Execute(insertCommand, entry, transaction);

            transaction.Commit();
        }

        public long GetSubInstanceId(long leaderboardId, long instanceId, long subLeaderboardId)
        {
            using NpgsqlConnection connection = GetConnection();
            return connection.QuerySingleOrDefault<long>(@"
                SELECT sub_instance_id
                FROM leaderboard_meta_entry
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId
                    AND sub_leaderboard_id = @SubLeaderboardId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId, SubLeaderboardId = subLeaderboardId });
        }

        public void InsertMetaEntries(List<DBMetaEntry> instances)
        {
            if (instances.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            InsertMetaEntries(connection, transaction, instances);
            transaction.Commit();
        }

        private static void InsertMetaEntries(NpgsqlConnection connection, NpgsqlTransaction transaction, List<DBMetaEntry> instances)
        {
            if (instances.Count == 0)
                return;

            connection.Execute(@"
                INSERT INTO leaderboard_meta_entry
                    (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id)
                VALUES
                    (@LeaderboardId, @InstanceId, @SubLeaderboardId, @SubInstanceId)", instances, transaction);
        }

        public List<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId)
        {
            using NpgsqlConnection connection = GetConnection();
            return connection.Query<DBMetaEntry>(@"
                SELECT leaderboard_id AS ""LeaderboardId"", instance_id AS ""InstanceId"",
                    sub_leaderboard_id AS ""SubLeaderboardId"", sub_instance_id AS ""SubInstanceId""
                FROM leaderboard_meta_entry
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId }).ToList();
        }

        public void InsertRewards(List<DBRewardEntry> dbRewards)
        {
            if (dbRewards.Count == 0)
                return;

            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            connection.Execute(@"
                INSERT INTO leaderboard_reward
                    (leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date)
                VALUES
                    (@LeaderboardId, @InstanceId, @ParticipantId, @RewardId, @Rank, @CreationDate)", dbRewards, transaction);
            transaction.Commit();
        }

        public List<DBRewardEntry> GetRewards(long participantId)
        {
            using NpgsqlConnection connection = GetConnection();
            return connection.Query<DBRewardEntry>(@"
                SELECT leaderboard_id AS ""LeaderboardId"", instance_id AS ""InstanceId"",
                    reward_id AS ""RewardId"", participant_id AS ""ParticipantId"", rank AS ""Rank"",
                    creation_date AS ""CreationDate"", rewarded_date AS ""RewardedDate""
                FROM leaderboard_reward
                WHERE participant_id = @ParticipantId AND rewarded_date IS NULL",
                new { ParticipantId = participantId }).ToList();
        }

        public void UpdateReward(DBRewardEntry reward)
        {
            using NpgsqlConnection connection = GetConnection();
            using NpgsqlTransaction transaction = connection.BeginTransaction();
            connection.Execute(@"
                UPDATE leaderboard_reward SET rewarded_date = @RewardedDate
                WHERE leaderboard_id = @LeaderboardId AND instance_id = @InstanceId
                    AND participant_id = @ParticipantId", reward, transaction);
            transaction.Commit();
        }

        private bool TryLoadConnectionInfo()
        {
            if (_connectionInfo != null)
                return true;

            string connectionString = ConfigManager.Instance.GetConfig<PostgreSQLDBManagerConfig>().ConnectionString;
            return PostgreSQLConnectionInfo.TryParse(connectionString, out _connectionInfo, out _);
        }

        private NpgsqlConnection GetConnection()
        {
            if (_connectionInfo == null)
                throw new InvalidOperationException("PostgreSQL leaderboard manager is not initialized.");

            NpgsqlConnection connection = new(_connectionInfo.ConnectionString);
            connection.Open();
            return connection;
        }

        internal string CreateInitializationErrorLogMessage(Exception exception)
        {
            string category;
            string sqlState = null;

            if (exception is PostgresException postgresException)
            {
                sqlState = postgresException.SqlState;
                if (sqlState.StartsWith("08", StringComparison.Ordinal))
                    category = "connection failure";
                else if (sqlState.StartsWith("28", StringComparison.Ordinal))
                    category = "authentication failure";
                else if (sqlState == PostgresErrorCodes.UniqueViolation)
                    category = "schema uniqueness failure";
                else
                    category = "schema or migration failure";
            }
            else if (exception is NpgsqlException)
            {
                category = "connection failure";
            }
            else if (exception is InvalidOperationException)
            {
                category = "schema-version failure";
            }
            else if (exception is ArgumentException)
            {
                category = "configuration failure";
            }
            else
            {
                category = "unexpected failure";
            }

            string message = $"{nameof(Initialize)}(): PostgreSQL leaderboard error for database {_connectionInfo?.Description ?? "unconfigured"}: {category}";
            return sqlState == null ? message : $"{message} (SQLSTATE {sqlState})";
        }

        private void LogInitializationFailure(Exception exception)
        {
            Logger.Error(CreateInitializationErrorLogMessage(exception));
        }
    }
}
