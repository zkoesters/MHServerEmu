using Dapper;
using System.Data;
using System.Data.SQLite;
using Gazillion;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess.SQLite
{
    public class SQLiteLeaderboardDBManager : ILeaderboardStore
    {
        private const int CurrentSchemaVersion = 1;         // Increment this when making changes to the database schema

        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly Lazy<IReadOnlyDictionary<string, string>> SchemaVersionOneMetadata = new(CreateSchemaVersionOneMetadata);
        [ThreadStatic] private static Action ReconciliationStateReadHook;
        [ThreadStatic] private static Action LifecyclePreCommitHook;
        [ThreadStatic] private static Action LifecycleCommitHook;
        public static SQLiteLeaderboardDBManager Instance { get; } = new();

        private string _dbFilePath;
        private string _connectionString;

        static SQLiteLeaderboardDBManager()
        {
            ReconciliationStateReadHook = null;
            LifecyclePreCommitHook = null;
            LifecycleCommitHook = null;
        }

        private SQLiteLeaderboardDBManager() { }

        public SQLiteLeaderboardDBManager(string databaseFilePath)
        {
            _dbFilePath = databaseFilePath ?? throw new ArgumentNullException(nameof(databaseFilePath));
            _connectionString = $"Data Source={_dbFilePath}";
        }

        public LeaderboardStoreResult Initialize()
        {
            return InitializeStore(out _);
        }

        public bool Initialize(string configPath, ref bool noTables)
        {
            _dbFilePath = configPath;
            _connectionString = $"Data Source={_dbFilePath}";
            return InitializeStore(out noTables) == LeaderboardStoreResult.Success;
        }

        private LeaderboardStoreResult InitializeStore(out bool created)
        {
            created = false;
            if (string.IsNullOrWhiteSpace(_dbFilePath))
                return LeaderboardStoreResult.Failed;

            try
            {
                if (File.Exists(_dbFilePath) == false)
                {
                    string parentDirectory = Path.GetDirectoryName(_dbFilePath);
                    if (string.IsNullOrEmpty(parentDirectory) == false)
                        Directory.CreateDirectory(parentDirectory);

                    if (InitializeDatabaseFile() == false)
                        return LeaderboardStoreResult.Failed;

                    created = true;
                    return LeaderboardStoreResult.Success;
                }

                using SQLiteConnection connection = GetConnection();
                long schemaVersion = connection.QuerySingle<long>("PRAGMA user_version");
                return schemaVersion == CurrentSchemaVersion && HasSchemaVersionOneMetadata(connection)
                    ? LeaderboardStoreResult.Success
                    : LeaderboardStoreResult.Failed;
            }
            catch (Exception e)
            {
                Logger.Error($"Initialize(): {e.Message}");
                return LeaderboardStoreResult.Failed;
            }
        }

        /// <summary>
        /// Initializes a new empty database file using the current schema.
        /// </summary>
        private bool InitializeDatabaseFile()
        {
            string initializationScript = SQLiteScripts.GetLeaderboardsScript();
            if (initializationScript == string.Empty)
                return Logger.ErrorReturn(false, "InitializeDatabaseFile(): Failed to get database initialization script");

            SQLiteConnection.CreateFile(_dbFilePath);
            using SQLiteConnection connection = GetConnection();
            connection.Execute(initializationScript);

            Logger.Info($"Initialized a new database file at {Path.GetRelativePath(FileHelper.ServerRoot, _dbFilePath)} using schema version {CurrentSchemaVersion}");            

            return true;
        }

        /// <summary>
        /// Creates and opens a new <see cref="SQLiteConnection"/>.
        /// </summary>
        private SQLiteConnection GetConnection()
        {
            SQLiteConnection connection = new(_connectionString);
            connection.Open();
            return connection;
        }

        private static bool HasSchemaVersionOneMetadata(SQLiteConnection connection)
        {
            return ReadSchemaMetadata(connection).OrderBy(pair => pair.Key).SequenceEqual(SchemaVersionOneMetadata.Value.OrderBy(pair => pair.Key));
        }

        private static IReadOnlyDictionary<string, string> CreateSchemaVersionOneMetadata()
        {
            using SQLiteConnection connection = new("Data Source=:memory:");
            connection.Open();
            connection.Execute(SQLiteScripts.GetLeaderboardsScript());
            return ReadSchemaMetadata(connection);
        }

        private static IReadOnlyDictionary<string, string> ReadSchemaMetadata(SQLiteConnection connection)
        {
            return connection.Query<SQLiteSchemaObject>(@"
                SELECT Type, Name, Sql FROM sqlite_master
                WHERE Type IN ('table', 'index', 'trigger') AND Name NOT LIKE 'sqlite_%'")
                .ToDictionary(schema => $"{schema.Type}:{schema.Name}", schema => NormalizeSchemaSql(schema.Sql));
        }

        private static string NormalizeSchemaSql(string sql)
        {
            return new string(sql.Where(character => char.IsWhiteSpace(character) == false && character != '"' && character != '[' && character != ']').ToArray())
                .ToUpperInvariant();
        }

        private sealed class SQLiteSchemaObject
        {
            public string Type { get; set; }
            public string Name { get; set; }
            public string Sql { get; set; }
        }

        public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries)
        {
            entries = Array.Empty<DBLeaderboardEntry>();
            try
            {
                using SQLiteConnection connection = GetConnection();
                if (connection.QuerySingleOrDefault<long?>("SELECT InstanceId FROM Instances WHERE InstanceId = @InstanceId", new { InstanceId = instanceId }) == null)
                    return LeaderboardStoreResult.NotFound;

                entries = connection.Query<DBLeaderboardEntry>("SELECT * FROM Entries WHERE InstanceId = @InstanceId", new { InstanceId = instanceId })
                    .Select(CloneEntry)
                    .ToArray();
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"LoadEntries(): {e.Message}");
                return LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance)
        {
            instance = null;
            try
            {
                using SQLiteConnection connection = GetConnection();
                DBLeaderboardInstance loaded = connection.QueryFirstOrDefault<DBLeaderboardInstance>(@"
                    SELECT * FROM Instances
                    WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId",
                    new { LeaderboardId = leaderboardId, InstanceId = instanceId });
                if (loaded == null)
                    return LeaderboardStoreResult.NotFound;

                instance = CloneInstance(loaded);
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"LoadInstance(): {e.Message}");
                return LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances)
        {
            instances = Array.Empty<DBLeaderboardInstance>();
            try
            {
                LeaderboardStoreValidator.ValidateVisibleInstancesLimit(limit);
            }
            catch (ArgumentOutOfRangeException)
            {
                return LeaderboardStoreResult.InvalidData;
            }

            try
            {
                using SQLiteConnection connection = GetConnection();
                instances = connection.Query<DBLeaderboardInstance>(@"
                    SELECT * FROM Instances
                    WHERE LeaderboardId = @LeaderboardId AND Visible = 1
                      AND (@BeforeInstanceId = 0
                        OR (@BeforeInstanceId < 0 AND (InstanceId < @BeforeInstanceId OR InstanceId >= 0))
                        OR (@BeforeInstanceId > 0 AND InstanceId >= 0 AND InstanceId < @BeforeInstanceId))
                    ORDER BY CASE WHEN InstanceId < 0 THEN 0 ELSE 1 END, InstanceId DESC
                    LIMIT @Limit",
                    new { LeaderboardId = leaderboardId, BeforeInstanceId = beforeInstanceId, Limit = limit })
                    .Select(CloneInstance)
                    .ToArray();
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"LoadVisibleInstances(): {e.Message}");
                return LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot)
        {
            snapshot = new();
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            try
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                List<DBLeaderboard> currentDefinitions = connection.Query<DBLeaderboard>("SELECT * FROM Leaderboards", transaction: transaction).ToList();
                List<DBLeaderboardInstance> currentInstances = connection.Query<DBLeaderboardInstance>("SELECT * FROM Instances", transaction: transaction).ToList();
                ReconciliationStateReadHook?.Invoke();

                if (TryValidateReconciliation(request, currentDefinitions, currentInstances, out Dictionary<long, LeaderboardDefinitionSpec> desiredDefinitions,
                    out Dictionary<long, LeaderboardInstanceSpec> initialInstances, out Dictionary<long, long> nextInstanceIds) == false)
                    return LeaderboardStoreResult.InvalidData;

                Dictionary<long, DBLeaderboard> currentById = currentDefinitions.ToDictionary(definition => definition.LeaderboardId);
                if (HasActiveInstanceOwnershipMismatch(currentDefinitions, currentInstances))
                    return LeaderboardStoreResult.InvalidData;

                Dictionary<long, long> activeInstanceIds = new();
                foreach ((long leaderboardId, LeaderboardDefinitionSpec definition) in desiredDefinitions)
                {
                    if (currentById.TryGetValue(leaderboardId, out DBLeaderboard current) == false || (current.IsEnabled == false && definition.IsEnabled))
                        activeInstanceIds.Add(leaderboardId, nextInstanceIds[leaderboardId]);
                    else
                        activeInstanceIds.Add(leaderboardId, current.ActiveInstanceId);
                }

                if (TryValidateTopology(request.MetaMappings, desiredDefinitions.Keys, activeInstanceIds) == false)
                    return LeaderboardStoreResult.InvalidData;

                foreach (LeaderboardDefinitionSpec definition in desiredDefinitions.Values.OrderBy(definition => unchecked((ulong)definition.LeaderboardId)))
                {
                    bool exists = currentById.TryGetValue(definition.LeaderboardId, out DBLeaderboard current);
                    bool createInitial = exists == false;
                    bool reenable = exists && current.IsEnabled == false && definition.IsEnabled;
                    long activeInstanceId = createInitial || reenable ? nextInstanceIds[definition.LeaderboardId] : current.ActiveInstanceId;

                    if (createInitial)
                    {
                        connection.Execute(@"
                            INSERT INTO Leaderboards (LeaderboardId, PrototypeName, ActiveInstanceId, IsEnabled, StartTime, MaxResetCount)
                            VALUES (@LeaderboardId, @PrototypeName, @ActiveInstanceId, @IsEnabled, @StartTime, @MaxResetCount)",
                            new
                            {
                                definition.LeaderboardId,
                                definition.PrototypeName,
                                ActiveInstanceId = activeInstanceId,
                                definition.IsEnabled,
                                definition.StartTime,
                                definition.MaxResetCount
                            }, transaction);
                    }
                    else
                    {
                        connection.Execute(@"
                            UPDATE Leaderboards
                            SET PrototypeName = @PrototypeName, ActiveInstanceId = @ActiveInstanceId, IsEnabled = @IsEnabled,
                                StartTime = @StartTime, MaxResetCount = @MaxResetCount
                            WHERE LeaderboardId = @LeaderboardId",
                            new
                            {
                                definition.LeaderboardId,
                                definition.PrototypeName,
                                ActiveInstanceId = activeInstanceId,
                                definition.IsEnabled,
                                definition.StartTime,
                                definition.MaxResetCount
                            }, transaction);
                    }

                    if (createInitial || reenable)
                    {
                        LeaderboardInstanceSpec initial = initialInstances[definition.LeaderboardId];
                        bool enabled = definition.IsEnabled;
                        connection.Execute(@"
                            INSERT INTO Instances (InstanceId, LeaderboardId, State, ActivationDate, Visible)
                            VALUES (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)",
                            new
                            {
                                InstanceId = activeInstanceId,
                                definition.LeaderboardId,
                                State = enabled ? 0 : 5,
                                ActivationDate = GetActivationDate(initial, request.CurrentTime),
                                Visible = enabled
                            }, transaction);
                    }
                    else if (definition.IsEnabled == false)
                    {
                        TerminalizeInstance(connection, transaction, current.LeaderboardId, current.ActiveInstanceId);
                    }
                    else if (definition.IsEnabled)
                    {
                        LeaderboardInstanceSpec initial = initialInstances[definition.LeaderboardId];
                        connection.Execute(@"
                            UPDATE Instances SET ActivationDate = @ActivationDate
                            WHERE LeaderboardId = @LeaderboardId AND State < 5 AND ActivationDate = 0",
                            new
                            {
                                definition.LeaderboardId,
                                ActivationDate = GetActivationDate(initial, request.CurrentTime)
                            }, transaction);
                    }
                }

                foreach (DBLeaderboard definition in currentDefinitions.Where(definition => desiredDefinitions.ContainsKey(definition.LeaderboardId) == false))
                {
                    connection.Execute("UPDATE Leaderboards SET IsEnabled = 0 WHERE LeaderboardId = @LeaderboardId", new { definition.LeaderboardId }, transaction);
                    TerminalizeInstance(connection, transaction, definition.LeaderboardId, definition.ActiveInstanceId);
                }

                foreach (long leaderboardId in desiredDefinitions.Keys)
                {
                    long activeInstanceId = activeInstanceIds[leaderboardId];
                    connection.Execute("DELETE FROM MetaEntries WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId",
                        new { LeaderboardId = leaderboardId, InstanceId = activeInstanceId }, transaction);
                }

                foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                {
                    connection.Execute(@"
                        INSERT INTO MetaEntries (LeaderboardId, InstanceId, SubLeaderboardId, SubInstanceId)
                        VALUES (@LeaderboardId, @InstanceId, @SubLeaderboardId, @SubInstanceId)", mapping, transaction);
                }

                snapshot = ReadSnapshot(connection, transaction, desiredDefinitions.Keys, request.NormalArchiveLimit);
                transaction.Commit();
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"ReconcileSchedule(): {e.Message}");
                snapshot = new();
                return LeaderboardStoreResult.Failed;
            }
        }

        private static bool TryValidateReconciliation(LeaderboardReconciliation request, IReadOnlyList<DBLeaderboard> currentDefinitions,
            IReadOnlyList<DBLeaderboardInstance> currentInstances, out Dictionary<long, LeaderboardDefinitionSpec> desiredDefinitions,
            out Dictionary<long, LeaderboardInstanceSpec> initialInstances, out Dictionary<long, long> nextInstanceIds)
        {
            desiredDefinitions = null;
            initialInstances = null;
            nextInstanceIds = null;

            if (request.NormalArchiveLimit < 0 || request.DesiredDefinitions.GroupBy(definition => definition.LeaderboardId).Any(group => group.Skip(1).Any())
                || request.DesiredDefinitions.GroupBy(definition => definition.PrototypeName, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
                return false;

            Dictionary<long, LeaderboardDefinitionSpec> requestedDefinitions = request.DesiredDefinitions.ToDictionary(definition => definition.LeaderboardId);
            if (request.InitialInstances.GroupBy(instance => instance.LeaderboardId).Any(group => group.Skip(1).Any())
                || request.InitialInstances.Any(instance => requestedDefinitions.ContainsKey(instance.LeaderboardId) == false)
                || requestedDefinitions.Keys.Any(leaderboardId => request.InitialInstances.Count(instance => instance.LeaderboardId == leaderboardId) != 1))
                return false;

            Dictionary<long, LeaderboardInstanceSpec> requestedInitialInstances = request.InitialInstances.ToDictionary(instance => instance.LeaderboardId);
            if (requestedInitialInstances.Any(pair => pair.Value.State != (requestedDefinitions[pair.Key].IsEnabled
                ? LeaderboardState.eLBS_Created
                : LeaderboardState.eLBS_Rewarded)
                || pair.Value.Visible != requestedDefinitions[pair.Key].IsEnabled))
                return false;

            Dictionary<long, DBLeaderboard> currentById = currentDefinitions.ToDictionary(definition => definition.LeaderboardId);
            HashSet<long> existingIds = currentInstances.Select(instance => instance.InstanceId).ToHashSet();
            HashSet<long> generatedIds = new();
            Dictionary<long, long> generatedInstanceIds = new();

            foreach (LeaderboardDefinitionSpec definition in requestedDefinitions.Values.OrderBy(definition => unchecked((ulong)definition.LeaderboardId)))
            {
                bool needsNewInstance = currentById.TryGetValue(definition.LeaderboardId, out DBLeaderboard current) == false
                    || (current.IsEnabled == false && definition.IsEnabled);
                if (needsNewInstance == false)
                    continue;

                IEnumerable<long> leaderboardInstances = currentInstances
                    .Where(instance => instance.LeaderboardId == definition.LeaderboardId)
                    .Select(instance => instance.InstanceId);
                if (LeaderboardInstanceIdGenerator.TryGetNext(definition.LeaderboardId, leaderboardInstances, out long instanceId) == false
                    || existingIds.Contains(instanceId) || generatedIds.Add(instanceId) == false)
                    return false;

                long requestedInstanceId = requestedInitialInstances[definition.LeaderboardId].InstanceId;
                if (requestedInstanceId != 0 && requestedInstanceId != instanceId)
                    return false;

                generatedInstanceIds.Add(definition.LeaderboardId, instanceId);
            }

            desiredDefinitions = requestedDefinitions;
            initialInstances = requestedInitialInstances;
            nextInstanceIds = generatedInstanceIds;
            return true;
        }

        private static bool TryValidateTopology(IReadOnlyList<LeaderboardMetaMapping> mappings, IEnumerable<long> desiredLeaderboardIds,
            IReadOnlyDictionary<long, long> activeInstanceIds)
        {
            HashSet<long> desired = desiredLeaderboardIds.ToHashSet();
            return mappings.GroupBy(mapping => (mapping.LeaderboardId, mapping.InstanceId, mapping.SubLeaderboardId)).All(group => group.Count() == 1)
                && mappings.All(mapping => desired.Contains(mapping.LeaderboardId)
                    && desired.Contains(mapping.SubLeaderboardId)
                    && activeInstanceIds[mapping.LeaderboardId] == mapping.InstanceId
                    && activeInstanceIds[mapping.SubLeaderboardId] == mapping.SubInstanceId);
        }

        private static bool HasActiveInstanceOwnershipMismatch(IEnumerable<DBLeaderboard> currentDefinitions,
            IReadOnlyList<DBLeaderboardInstance> currentInstances)
        {
            return currentDefinitions
                .Any(definition => currentInstances.Any(instance => instance.LeaderboardId == definition.LeaderboardId
                    && instance.InstanceId == definition.ActiveInstanceId) == false);
        }

        private static long GetActivationDate(LeaderboardInstanceSpec initial, long currentTime)
        {
            return initial.ActivationDate == 0 ? currentTime : initial.ActivationDate;
        }

        private static void TerminalizeInstance(SQLiteConnection connection, SQLiteTransaction transaction, long leaderboardId, long instanceId)
        {
            connection.Execute(@"
                UPDATE Instances
                SET State = 5,
                    Visible = CASE WHEN EXISTS (SELECT 1 FROM Rewards WHERE Rewards.InstanceId = Instances.InstanceId) THEN 1 ELSE 0 END
                WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId AND State < 5",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId }, transaction);
        }

        private static LeaderboardSnapshot ReadSnapshot(SQLiteConnection connection, SQLiteTransaction transaction, IEnumerable<long> desiredLeaderboardIds, int normalArchiveLimit)
        {
            long[] leaderboardIds = desiredLeaderboardIds.ToArray();
            List<DBLeaderboard> definitions = connection.Query<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId IN @LeaderboardIds",
                new { LeaderboardIds = leaderboardIds }, transaction).OrderBy(definition => unchecked((ulong)definition.LeaderboardId)).ToList();
            List<DBLeaderboardInstance> nonterminal = connection.Query<DBLeaderboardInstance>("SELECT * FROM Instances WHERE LeaderboardId IN @LeaderboardIds AND State < 5",
                new { LeaderboardIds = leaderboardIds }, transaction).OrderBy(instance => unchecked((ulong)instance.InstanceId)).ToList();
            List<DBLeaderboardInstance> normalArchives = connection.Query<DBLeaderboardInstance>("SELECT * FROM Instances WHERE LeaderboardId IN @LeaderboardIds AND State >= 5 AND Visible = 1",
                new { LeaderboardIds = leaderboardIds }, transaction)
                .GroupBy(instance => instance.LeaderboardId)
                .SelectMany(group => group.OrderByDescending(instance => unchecked((ulong)instance.InstanceId)).Take(normalArchiveLimit))
                .OrderBy(instance => unchecked((ulong)instance.LeaderboardId))
                .ThenByDescending(instance => unchecked((ulong)instance.InstanceId))
                .ToList();
            HashSet<long> snapshotInstanceIds = nonterminal.Concat(normalArchives).Select(instance => instance.InstanceId).ToHashSet();
            List<DBMetaEntry> mappings = connection.Query<DBMetaEntry>("SELECT * FROM MetaEntries WHERE LeaderboardId IN @LeaderboardIds",
                new { LeaderboardIds = leaderboardIds }, transaction)
                .Where(mapping => snapshotInstanceIds.Contains(mapping.InstanceId))
                .OrderBy(mapping => unchecked((ulong)mapping.LeaderboardId))
                .ThenBy(mapping => unchecked((ulong)mapping.InstanceId))
                .ThenBy(mapping => unchecked((ulong)mapping.SubLeaderboardId))
                .ToList();
            return new(definitions, nonterminal, normalArchives, mappings);
        }

        public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request)
        {
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            bool commitStarted = false;
            try
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                DBLeaderboard definition = connection.QueryFirstOrDefault<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId = @LeaderboardId",
                    new { request.LeaderboardId }, transaction);
                if (definition == null)
                    return LeaderboardStoreResult.NotFound;

                DBLeaderboardInstance target = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                    new { request.InstanceId }, transaction);
                if (target == null)
                    return LeaderboardStoreResult.NotFound;

                if (target.LeaderboardId != request.LeaderboardId)
                    return LeaderboardStoreResult.InvalidData;

                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId)
                    return LeaderboardStoreResult.StaleState;

                if (request.InstanceId != definition.ActiveInstanceId)
                    return LeaderboardStoreResult.StaleState;

                if (target.State == LeaderboardState.eLBS_Active)
                    return LeaderboardStoreResult.Success;

                if (target.State != request.ExpectedState)
                    return LeaderboardStoreResult.StaleState;

                connection.Execute("UPDATE Instances SET State = @State WHERE InstanceId = @InstanceId",
                    new { State = (int)LeaderboardState.eLBS_Active, request.InstanceId }, transaction);
                CommitLifecycleTransaction(transaction, ref commitStarted);
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"ActivateInstance(): {e.Message}");
                return commitStarted ? LeaderboardStoreResult.OutcomeUncertain : LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request)
        {
            if (request == null || request.Entries.GroupBy(entry => entry.ParticipantId).Any(group => group.Skip(1).Any()))
                return LeaderboardStoreResult.InvalidData;

            bool commitStarted = false;
            try
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                DBLeaderboard definition = connection.QueryFirstOrDefault<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId = @LeaderboardId",
                    new { request.LeaderboardId }, transaction);
                if (definition == null)
                    return LeaderboardStoreResult.NotFound;

                DBLeaderboardInstance instance = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                    new { request.InstanceId }, transaction);
                if (instance == null)
                    return LeaderboardStoreResult.NotFound;

                if (instance.LeaderboardId != request.LeaderboardId)
                    return LeaderboardStoreResult.InvalidData;

                if (definition.ActiveInstanceId != request.InstanceId || instance.State != request.ExpectedState)
                    return LeaderboardStoreResult.StaleState;

                List<LeaderboardEntryWrite> missingEntries = new();
                List<LeaderboardEntryWrite> existingEntries = new();
                foreach (LeaderboardEntryWrite entry in request.Entries)
                {
                    DBLeaderboardEntry existing = connection.QueryFirstOrDefault<DBLeaderboardEntry>(@"
                        SELECT * FROM Entries WHERE InstanceId = @InstanceId AND ParticipantId = @ParticipantId",
                        new { entry.InstanceId, entry.ParticipantId }, transaction);
                    if (existing == null)
                    {
                        missingEntries.Add(entry);
                        continue;
                    }

                    if (existing.RuleStates == null)
                        return LeaderboardStoreResult.InvalidData;

                    existingEntries.Add(entry);
                }

                foreach (LeaderboardEntryWrite entry in missingEntries)
                {
                    connection.Execute(@"
                        INSERT INTO Entries (InstanceId, ParticipantId, Score, HighScore, RuleStates)
                        VALUES (@InstanceId, @ParticipantId, @Score, @HighScore, @RuleStates)",
                        new { entry.InstanceId, entry.ParticipantId, entry.Score, entry.HighScore, RuleStates = entry.RuleStates }, transaction);
                }

                foreach (LeaderboardEntryWrite entry in existingEntries)
                {
                    connection.Execute(@"
                        UPDATE Entries SET Score = @Score, HighScore = @HighScore, RuleStates = @RuleStates
                        WHERE InstanceId = @InstanceId AND ParticipantId = @ParticipantId",
                        new { entry.InstanceId, entry.ParticipantId, entry.Score, entry.HighScore, RuleStates = entry.RuleStates }, transaction);
                }

                CommitLifecycleTransaction(transaction, ref commitStarted);
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"SaveScoreBatch(): {e.Message}");
                return commitStarted ? LeaderboardStoreResult.OutcomeUncertain : LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request)
        {
            if (request == null || request.Entries.GroupBy(entry => entry.ParticipantId).Any(group => group.Skip(1).Any()))
                return LeaderboardStoreResult.InvalidData;

            bool commitStarted = false;
            try
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                DBLeaderboard definition = connection.QueryFirstOrDefault<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId = @LeaderboardId",
                    new { request.LeaderboardId }, transaction);
                if (definition == null)
                    return LeaderboardStoreResult.NotFound;

                DBLeaderboardInstance instance = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                    new { request.InstanceId }, transaction);
                if (instance == null)
                    return LeaderboardStoreResult.NotFound;

                if (instance.LeaderboardId != request.LeaderboardId)
                    return LeaderboardStoreResult.InvalidData;

                List<DBLeaderboardEntry> existingEntries = connection.Query<DBLeaderboardEntry>("SELECT * FROM Entries WHERE InstanceId = @InstanceId",
                    new { request.InstanceId }, transaction).ToList();
                if (existingEntries.Any(entry => entry.RuleStates == null))
                    return LeaderboardStoreResult.InvalidData;
                if (instance.State == LeaderboardState.eLBS_Expired)
                    return HasExactEntries(existingEntries, request.Entries) ? LeaderboardStoreResult.Success : LeaderboardStoreResult.Conflict;

                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId || request.InstanceId != definition.ActiveInstanceId
                    || instance.State != request.ExpectedState)
                    return LeaderboardStoreResult.StaleState;

                Dictionary<long, LeaderboardEntryWrite> requestedEntries = request.Entries.ToDictionary(entry => entry.ParticipantId);
                if (existingEntries.Any(entry => requestedEntries.TryGetValue(entry.ParticipantId, out LeaderboardEntryWrite requested) == false
                    || EntriesMatch(entry, requested) == false))
                    return LeaderboardStoreResult.Conflict;

                foreach (LeaderboardEntryWrite entry in request.Entries.Where(entry => existingEntries.Any(existing => existing.ParticipantId == entry.ParticipantId) == false))
                {
                    connection.Execute(@"
                        INSERT INTO Entries (InstanceId, ParticipantId, Score, HighScore, RuleStates)
                        VALUES (@InstanceId, @ParticipantId, @Score, @HighScore, @RuleStates)",
                        new { entry.InstanceId, entry.ParticipantId, entry.Score, entry.HighScore, RuleStates = entry.RuleStates }, transaction);
                }

                connection.Execute("UPDATE Instances SET State = @State WHERE InstanceId = @InstanceId",
                    new { State = (int)LeaderboardState.eLBS_Expired, request.InstanceId }, transaction);
                CommitLifecycleTransaction(transaction, ref commitStarted);
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"ExpireInstance(): {e.Message}");
                return commitStarted ? LeaderboardStoreResult.OutcomeUncertain : LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance)
        {
            committedInstance = null;
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            bool commitStarted = false;
            try
            {
                using SQLiteConnection connection = GetConnection();
                using SQLiteTransaction transaction = connection.BeginTransaction(IsolationLevel.Serializable);
                DBLeaderboard definition = connection.QueryFirstOrDefault<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId = @LeaderboardId",
                    new { request.LeaderboardId }, transaction);
                if (definition == null)
                    return LeaderboardStoreResult.NotFound;

                DBLeaderboardInstance previous = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                    new { InstanceId = request.ExpectedActiveInstanceId }, transaction);
                if (previous == null)
                    return LeaderboardStoreResult.NotFound;

                if (previous.LeaderboardId != request.LeaderboardId)
                    return LeaderboardStoreResult.InvalidData;

                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId)
                {
                    if (request.NextInstance.InstanceId != 0 && definition.ActiveInstanceId != request.NextInstance.InstanceId)
                        return LeaderboardStoreResult.StaleState;

                    if (request.NextInstance.InstanceId == 0
                        && (LeaderboardInstanceIdGenerator.TryGetNext(request.LeaderboardId, new[] { request.ExpectedActiveInstanceId }, out long expectedReplayInstanceId) == false
                            || definition.ActiveInstanceId != expectedReplayInstanceId))
                        return LeaderboardStoreResult.StaleState;

                    DBLeaderboardInstance replay = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                        new { InstanceId = definition.ActiveInstanceId }, transaction);
                    if (replay == null || replay.LeaderboardId != request.LeaderboardId || previous.State != request.PreviousState
                        || MatchesInstance(replay, request.NextInstance) == false)
                        return LeaderboardStoreResult.Conflict;

                    List<DBMetaEntry> replayMappings = connection.Query<DBMetaEntry>(@"
                        SELECT * FROM MetaEntries WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId",
                        new { request.LeaderboardId, InstanceId = replay.InstanceId }, transaction).ToList();
                    if (HasExactMappings(replayMappings, request.MetaMappings, replay.InstanceId) == false)
                        return LeaderboardStoreResult.Conflict;

                    committedInstance = CloneInstance(replay);
                    return LeaderboardStoreResult.Success;
                }

                if (previous.State != request.ExpectedActiveState)
                    return LeaderboardStoreResult.StaleState;

                List<long> leaderboardInstanceIds = connection.Query<long>("SELECT InstanceId FROM Instances WHERE LeaderboardId = @LeaderboardId",
                    new { request.LeaderboardId }, transaction).ToList();
                if (LeaderboardInstanceIdGenerator.TryGetNext(request.LeaderboardId, new[] { request.ExpectedActiveInstanceId }, out long expectedGeneratedInstanceId) == false
                    || LeaderboardInstanceIdGenerator.TryGetNext(request.LeaderboardId, leaderboardInstanceIds, out long generatedInstanceId) == false
                    || generatedInstanceId != expectedGeneratedInstanceId
                    || (request.NextInstance.InstanceId != 0 && request.NextInstance.InstanceId != generatedInstanceId)
                    || connection.QuerySingleOrDefault<long?>("SELECT InstanceId FROM Instances WHERE InstanceId = @InstanceId", new { InstanceId = generatedInstanceId }, transaction) != null)
                    return LeaderboardStoreResult.InvalidData;

                foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                {
                    DBLeaderboard subDefinition = connection.QueryFirstOrDefault<DBLeaderboard>("SELECT * FROM Leaderboards WHERE LeaderboardId = @LeaderboardId",
                        new { LeaderboardId = mapping.SubLeaderboardId }, transaction);
                    DBLeaderboardInstance subInstance = connection.QueryFirstOrDefault<DBLeaderboardInstance>("SELECT * FROM Instances WHERE InstanceId = @InstanceId",
                        new { InstanceId = mapping.SubInstanceId }, transaction);
                    if (subDefinition == null || subInstance == null || subDefinition.ActiveInstanceId != mapping.SubInstanceId
                        || subInstance.LeaderboardId != mapping.SubLeaderboardId)
                        return LeaderboardStoreResult.InvalidData;
                }

                connection.Execute(@"
                    INSERT INTO Instances (InstanceId, LeaderboardId, State, ActivationDate, Visible)
                    VALUES (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)",
                    new
                    {
                        InstanceId = generatedInstanceId,
                        request.LeaderboardId,
                        State = (int)request.NextState,
                        request.NextInstance.ActivationDate,
                        request.NextInstance.Visible
                    }, transaction);
                connection.Execute("UPDATE Instances SET State = @State WHERE InstanceId = @InstanceId",
                    new { State = (int)request.PreviousState, InstanceId = request.ExpectedActiveInstanceId }, transaction);
                connection.Execute("UPDATE Leaderboards SET ActiveInstanceId = @ActiveInstanceId WHERE LeaderboardId = @LeaderboardId",
                    new { ActiveInstanceId = generatedInstanceId, request.LeaderboardId }, transaction);
                foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                {
                    connection.Execute(@"
                        INSERT INTO MetaEntries (LeaderboardId, InstanceId, SubLeaderboardId, SubInstanceId)
                        VALUES (@LeaderboardId, @InstanceId, @SubLeaderboardId, @SubInstanceId)",
                        new { request.LeaderboardId, InstanceId = generatedInstanceId, mapping.SubLeaderboardId, mapping.SubInstanceId }, transaction);
                }

                committedInstance = new()
                {
                    InstanceId = generatedInstanceId,
                    LeaderboardId = request.LeaderboardId,
                    State = request.NextState,
                    ActivationDate = request.NextInstance.ActivationDate,
                    Visible = request.NextInstance.Visible
                };
                CommitLifecycleTransaction(transaction, ref commitStarted);
                return LeaderboardStoreResult.Success;
            }
            catch (Exception e)
            {
                Logger.Error($"RotateActiveInstance(): {e.Message}");
                committedInstance = null;
                return commitStarted ? LeaderboardStoreResult.OutcomeUncertain : LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot)
        {
            snapshot = new();
            return LeaderboardStoreResult.Failed;
        }

        public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Failed;

        public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards)
        {
            rewards = Array.Empty<DBRewardEntry>();
            return LeaderboardStoreResult.Failed;
        }

        public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Failed;

        private static bool HasExactEntries(IReadOnlyCollection<DBLeaderboardEntry> existingEntries, IReadOnlyList<LeaderboardEntryWrite> requestedEntries)
        {
            return existingEntries.Count == requestedEntries.Count
                && requestedEntries.All(requested => existingEntries.Any(existing => existing.ParticipantId == requested.ParticipantId && EntriesMatch(existing, requested)));
        }

        private static bool EntriesMatch(DBLeaderboardEntry existing, LeaderboardEntryWrite requested)
        {
            return existing.Score == requested.Score && existing.HighScore == requested.HighScore
                && existing.RuleStates != null && existing.RuleStates.SequenceEqual(requested.RuleStates);
        }

        private static bool MatchesInstance(DBLeaderboardInstance existing, LeaderboardInstanceSpec requested)
        {
            return existing.LeaderboardId == requested.LeaderboardId && existing.State == requested.State
                && existing.ActivationDate == requested.ActivationDate && existing.Visible == requested.Visible;
        }

        private static bool HasExactMappings(IReadOnlyCollection<DBMetaEntry> existingMappings, IReadOnlyList<LeaderboardMetaMapping> requestedMappings, long instanceId)
        {
            return existingMappings.Count == requestedMappings.Count
                && requestedMappings.All(requested => existingMappings.Any(existing => existing.LeaderboardId == requested.LeaderboardId
                    && existing.InstanceId == instanceId && existing.SubLeaderboardId == requested.SubLeaderboardId && existing.SubInstanceId == requested.SubInstanceId));
        }

        private static void CommitLifecycleTransaction(SQLiteTransaction transaction, ref bool commitStarted)
        {
            LifecyclePreCommitHook?.Invoke();
            commitStarted = true;
            transaction.Commit();
            LifecycleCommitHook?.Invoke();
        }

        private static DBLeaderboardEntry CloneEntry(DBLeaderboardEntry entry)
        {
            return new()
            {
                InstanceId = entry.InstanceId,
                ParticipantId = entry.ParticipantId,
                Score = entry.Score,
                HighScore = entry.HighScore,
                RuleStates = entry.RuleStates?.ToArray()
            };
        }

        private static DBLeaderboardInstance CloneInstance(DBLeaderboardInstance instance)
        {
            return new()
            {
                InstanceId = instance.InstanceId,
                LeaderboardId = instance.LeaderboardId,
                State = instance.State,
                ActivationDate = instance.ActivationDate,
                Visible = instance.Visible
            };
        }

        public void InsertLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0) return;
            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            connection.Execute(@"
                INSERT INTO Leaderboards 
                (LeaderboardId, PrototypeName, ActiveInstanceId, IsEnabled, StartTime, MaxResetCount)
                VALUES 
                (@LeaderboardId, @PrototypeName, @ActiveInstanceId, @IsEnabled, @StartTime, @MaxResetCount)",
                dbLeaderboards, transaction);

            transaction.Commit();
        }

        public void UpdateLeaderboards(List<DBLeaderboard> dbLeaderboards)
        {
            if (dbLeaderboards.Count == 0) return;
            using SQLiteConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE Leaderboards 
                SET 
                    ActiveInstanceId = @ActiveInstanceId,
                    IsEnabled = @IsEnabled,
                    StartTime = @StartTime,
                    MaxResetCount = @MaxResetCount
                WHERE LeaderboardId = @LeaderboardId",
                dbLeaderboards);
        }

        public DBLeaderboard[] GetLeaderboards()
        {
            using SQLiteConnection connection = GetConnection();
            return connection.Query<DBLeaderboard>("SELECT * FROM Leaderboards").ToArray();
        }

        public bool UpdateActiveInstanceState(long leaderboardId, long activeInstanceId, int state)
        {
            using SQLiteConnection connection = GetConnection();

            int rows = connection.Execute(@"
                UPDATE Leaderboards SET ActiveInstanceId = @ActiveInstanceId 
                WHERE LeaderboardId = @LeaderboardId",
                new { LeaderboardId = leaderboardId, ActiveInstanceId = activeInstanceId });

            rows += DoUpdateInstanceState(connection, activeInstanceId, state);

            return rows == 2;
        }

        public List<DBLeaderboardInstance> GetInstances(long leaderboardId, int maxArchivedInstances)
        {
            using SQLiteConnection connection = GetConnection();

            // Get active instances
            List<DBLeaderboardInstance> instanceList = new(
                connection.Query<DBLeaderboardInstance>(@"
                    SELECT * FROM Instances 
                    WHERE LeaderboardId = @LeaderboardId AND State <= 1 
                    ORDER BY InstanceId DESC",
                    new { LeaderboardId = leaderboardId }));

            // Update visibility of archived instances
            UpdateArchivedInstanceVisibility(connection, leaderboardId, maxArchivedInstances);

            // Get visible archived instances
            IEnumerable<DBLeaderboardInstance> archivedInstances = connection.Query<DBLeaderboardInstance>(@"
                SELECT * FROM Instances 
                WHERE LeaderboardId = @LeaderboardId AND State > 1 AND Visible = 1
                ORDER BY InstanceId DESC
                LIMIT @MaxArchivedInstances",
                new { LeaderboardId = leaderboardId, MaxArchivedInstances = maxArchivedInstances });

            instanceList.AddRange(archivedInstances);

            return instanceList;
        }

        public DBLeaderboardInstance GetInstance(long leaderboardId, long instanceId)
        {
            using SQLiteConnection connection = GetConnection();
            return connection.QueryFirstOrDefault<DBLeaderboardInstance>(@"
                SELECT * FROM Instances
                WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId });
        }

        /// <summary>
        /// Updates archived leaderboard visiblity and retrieves 
        /// </summary>
        private void UpdateArchivedInstanceVisibility(SQLiteConnection connection, long leaderboardId, int maxArchivedInstances)
        {
            // Make archived instances that had no participants invisible
            connection.Execute(@"
                UPDATE Instances 
                SET Visible = 0 
                WHERE LeaderboardId = @LeaderboardId AND State > 1 AND Visible = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM Entries 
                      WHERE Entries.InstanceId = Instances.InstanceId
                  )",
                new { LeaderboardId = leaderboardId });

            // Get the most recent archived instances
            List<long> excludedInstanceIds = connection.Query<long>(@"
                SELECT InstanceId 
                FROM Instances 
                WHERE LeaderboardId = @LeaderboardId AND State > 1 AND Visible = 1
                ORDER BY InstanceId DESC
                LIMIT @MaxArchivedInstances",
                new { LeaderboardId = leaderboardId, MaxArchivedInstances = maxArchivedInstances }).ToList();

            if (excludedInstanceIds.Count == 0)
                excludedInstanceIds.Add(0);

            // Make non-recent archived instances that have no rewards invisible
            connection.Execute(@"
                UPDATE Instances 
                SET Visible = 0 
                WHERE LeaderboardId = @LeaderboardId AND State > 1 AND Visible = 1
                  AND InstanceId NOT IN @ExcludedInstanceIds
                  AND NOT EXISTS (
                      SELECT 1 FROM Rewards 
                      WHERE Rewards.LeaderboardId = Instances.LeaderboardId 
                        AND Rewards.InstanceId = Instances.InstanceId 
                        AND Rewards.RewardedDate IS NOT NULL
                  )",
                new { LeaderboardId = leaderboardId, ExcludedInstanceIds = excludedInstanceIds });
        }

        public void UpdateOrInsertInstances(List<DBLeaderboardInstance> dbInstances)
        {
            if (dbInstances.Count == 0)
                return;

            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            const string updateCommand = @"
                UPDATE Instances
                SET State = @State, ActivationDate = @ActivationDate, Visible = @Visible
                WHERE InstanceId = @InstanceId";

            const string insertCommand = @"
                INSERT INTO Instances (InstanceId, LeaderboardId, State, ActivationDate, Visible) 
                VALUES (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible)";

            foreach (var instance in dbInstances)
                if (connection.Execute(updateCommand, instance, transaction) == 0)
                    connection.Execute(insertCommand, instance, transaction);

            transaction.Commit();
        }

        public void InsertInstance(DBLeaderboardInstance dbInstance)
        {
            using SQLiteConnection connection = GetConnection();

            connection.Execute(@"
                INSERT INTO Instances (InstanceId, LeaderboardId, State, ActivationDate, Visible) 
                VALUES (@InstanceId, @LeaderboardId, @State, @ActivationDate, @Visible);", dbInstance);
        }

        public void UpdateInstanceState(long instanceId, int state)
        {
            using SQLiteConnection connection = GetConnection();
            DoUpdateInstanceState(connection, instanceId, state);
        }

        private int DoUpdateInstanceState(SQLiteConnection connection, long instanceId, int state)
        {
            return connection.Execute(@"
                UPDATE Instances SET State = @State 
                WHERE InstanceId = @InstanceId",
                new { InstanceId = instanceId, State = state });
        }

        public void UpdateInstanceActivationDate(DBLeaderboardInstance dbInstance)
        {
            using SQLiteConnection connection = GetConnection();
            connection.Execute(@"
                UPDATE Instances SET ActivationDate = @ActivationDate 
                WHERE InstanceId = @InstanceId", dbInstance);
        }

        public List<DBLeaderboardEntry> GetEntries(long instanceId, bool ascending)
        {
            using SQLiteConnection connection = GetConnection();

            string order = ascending ? "ASC" : "DESC";

            return connection.Query<DBLeaderboardEntry>(@"
                SELECT * FROM Entries WHERE InstanceId = @InstanceId 
                ORDER BY HighScore " + order, 
                new { InstanceId = instanceId }).ToList();
        }

        public void UpdateOrInsertEntries(List<DBLeaderboardEntry> dbEntries)
        {
            if (dbEntries.Count == 0)
                return;

            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            const string updateCommand = @"
                UPDATE Entries
                SET Score = @Score, HighScore = @HighScore, RuleStates = @RuleStates
                WHERE InstanceId = @InstanceId AND ParticipantId = @ParticipantId";

            const string insertCommand = @"
                INSERT INTO Entries (InstanceId, ParticipantId, Score, HighScore, RuleStates)
                VALUES (@InstanceId, @ParticipantId, @Score, @HighScore, @RuleStates)";

            foreach (var entry in dbEntries)
                if (connection.Execute(updateCommand, entry, transaction) == 0)
                    connection.Execute(insertCommand, entry, transaction);

            transaction.Commit();
        }

        public long GetSubInstanceId(long leaderboardId, long instanceId, long subLeaderboardId)
        {
            using var connection = GetConnection();
            return connection.QuerySingleOrDefault<long>(@"
                SELECT SubInstanceId FROM MetaEntries
                WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId 
                AND SubLeaderboardId = @SubLeaderboardId",
                new { LeaderboardId = leaderboardId, InstanceId = instanceId, SubLeaderboardId = subLeaderboardId });
        }

        public void InsertMetaEntries(List<DBMetaEntry> instances)
        {
            if (instances.Count == 0)
                return;

            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            const string insertCommand = @"
                INSERT INTO MetaEntries (LeaderboardId, InstanceId, SubLeaderboardId, SubInstanceId)
                VALUES (@LeaderboardId, @InstanceId, @SubLeaderboardId, @SubInstanceId)";

            connection.Execute(insertCommand, instances, transaction);
            transaction.Commit();
        }

        public List<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId)
        {
            using var connection = GetConnection();
            return connection.Query<DBMetaEntry>(@"
                SELECT * FROM MetaEntries
                WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId", 
                new { LeaderboardId = leaderboardId, InstanceId = instanceId }).ToList();
        }

        public void InsertRewards(List<DBRewardEntry> dbRewards)
        {
            if (dbRewards.Count == 0) return;
            using var connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            connection.Execute(@"
                INSERT INTO Rewards (LeaderboardId, InstanceId, ParticipantId, RewardId, Rank, CreationDate)
                VALUES (@LeaderboardId, @InstanceId, @ParticipantId, @RewardId, @Rank, @CreationDate)", dbRewards, transaction);

            transaction.Commit();
        }

        public List<DBRewardEntry> GetRewards(long participantId)
        {
            using SQLiteConnection connection = GetConnection();

            return connection.Query<DBRewardEntry>(@"
                SELECT * FROM Rewards WHERE ParticipantId = @ParticipantId AND RewardedDate IS NULL",
                new { ParticipantId = (long)participantId }).ToList();
        }

        public void UpdateReward(DBRewardEntry reward)
        {
            using SQLiteConnection connection = GetConnection();
            using var transaction = connection.BeginTransaction();

            connection.Execute(@"
                UPDATE Rewards SET RewardedDate = @RewardedDate 
                WHERE LeaderboardId = @LeaderboardId AND InstanceId = @InstanceId AND ParticipantId = @ParticipantId", reward, transaction);

            transaction.Commit();
        }
    }
}
