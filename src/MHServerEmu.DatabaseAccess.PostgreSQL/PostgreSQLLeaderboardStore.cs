using Gazillion;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using Npgsql;
using NpgsqlTypes;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLLeaderboardStore : ILeaderboardStore
    {
        private const string DefinitionTable = "mhserveremu.leaderboard";
        private const string InstanceTable = "mhserveremu.leaderboard_instance";
        private const string EntryTable = "mhserveremu.leaderboard_entry";
        private const string MetaEntryTable = "mhserveremu.leaderboard_meta_entry";
        private const string RewardTable = "mhserveremu.leaderboard_reward";
        private const long ReconciliationLockKey = unchecked((long)0x4C6561646572626FUL);

        private static Action<string> SnapshotMetaMappingCommandHook = null;
        private static readonly AsyncLocal<Func<NpgsqlConnection, NpgsqlTransaction, Task>> LifecyclePreCommitHook = new();
        private readonly PostgreSQLStoreExecutor _executor;

        internal PostgreSQLLeaderboardStore(NpgsqlDataSource dataSource, PostgreSQLStoreExecutor executor)
        {
            ArgumentNullException.ThrowIfNull(dataSource);
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        internal static void SetLifecyclePreCommitHookForTest(Func<NpgsqlConnection, NpgsqlTransaction, Task> hook)
        {
            LifecyclePreCommitHook.Value = hook;
        }

        public LeaderboardStoreResult Initialize()
        {
            try
            {
                PostgreSQLReadResult<bool> read = _executor.ExecuteReadAsync("LeaderboardInitialize", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand command = new(@"SELECT to_regclass('mhserveremu.leaderboard') IS NOT NULL
                        AND to_regclass('mhserveremu.leaderboard_instance') IS NOT NULL
                        AND to_regclass('mhserveremu.leaderboard_entry') IS NOT NULL
                        AND to_regclass('mhserveremu.leaderboard_meta_entry') IS NOT NULL
                        AND to_regclass('mhserveremu.leaderboard_reward') IS NOT NULL", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    return (bool)await command.ExecuteScalarAsync(cancellationToken);
                }).GetAwaiter().GetResult();
                return read.Succeeded && read.Value ? LeaderboardStoreResult.Success : LeaderboardStoreResult.Failed;
            }
            catch
            {
                return LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries)
        {
            entries = Array.Empty<DBLeaderboardEntry>();
            try
            {
                PostgreSQLReadResult<LeaderboardEntriesRead> read = _executor.ExecuteReadAsync("LeaderboardLoadEntries", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand instanceCommand = new($"SELECT 1 FROM {InstanceTable} WHERE instance_id = @instanceId", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    instanceCommand.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
                    if (await instanceCommand.ExecuteScalarAsync(cancellationToken) == null)
                        return new LeaderboardEntriesRead(false, Array.Empty<DBLeaderboardEntry>());

                    await using NpgsqlCommand command = new($"SELECT instance_id, participant_id, score, high_score, rule_states FROM {EntryTable} WHERE instance_id = @instanceId", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
                    return new LeaderboardEntriesRead(true, await ReadEntriesAsync(command, cancellationToken));
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                {
                    entries = read.Value.Entries;
                    return read.Value.Found ? LeaderboardStoreResult.Success : LeaderboardStoreResult.NotFound;
                }
            }
            catch
            {
            }
            return LeaderboardStoreResult.Failed;
        }

        public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance)
        {
            instance = null;
            try
            {
                PostgreSQLReadResult<DBLeaderboardInstance> read = _executor.ExecuteReadAsync("LeaderboardLoadInstance", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand command = new($"SELECT instance_id, leaderboard_id, state, activation_date, visible FROM {InstanceTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
                    command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                    return await reader.ReadAsync(cancellationToken) ? ReadInstance(reader) : null;
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                {
                    instance = read.Value;
                    return instance == null ? LeaderboardStoreResult.NotFound : LeaderboardStoreResult.Success;
                }
            }
            catch
            {
            }
            return LeaderboardStoreResult.Failed;
        }

        public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings)
        {
            mappings = Array.Empty<LeaderboardMetaMapping>();
            try
            {
                PostgreSQLReadResult<(bool Found, IReadOnlyList<LeaderboardMetaMapping> Mappings)> read = _executor.ExecuteReadAsync("LeaderboardLoadMetaMappings", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand parent = new($"SELECT 1 FROM {InstanceTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    parent.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
                    parent.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
                    if (await parent.ExecuteScalarAsync(cancellationToken) == null)
                        return (false, (IReadOnlyList<LeaderboardMetaMapping>)Array.Empty<LeaderboardMetaMapping>());

                    await using NpgsqlCommand command = new($@"SELECT leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id FROM {MetaEntryTable}
                        WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId
                        ORDER BY sub_leaderboard_id", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
                    command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
                    List<LeaderboardMetaMapping> loaded = new();
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        loaded.Add(new LeaderboardMetaMapping(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
                    return (true, (IReadOnlyList<LeaderboardMetaMapping>)loaded.ToArray());
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                {
                    mappings = read.Value.Mappings;
                    return read.Value.Found ? LeaderboardStoreResult.Success : LeaderboardStoreResult.NotFound;
                }
            }
            catch
            {
            }
            return LeaderboardStoreResult.Failed;
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
                PostgreSQLReadResult<IReadOnlyList<DBLeaderboardInstance>> read = _executor.ExecuteReadAsync("LeaderboardLoadVisibleInstances", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand command = new($@"SELECT instance_id, leaderboard_id, state, activation_date, visible
                        FROM {InstanceTable}
                        WHERE leaderboard_id = @leaderboardId AND visible
                          AND (@beforeInstanceId = 0
                            OR (@beforeInstanceId < 0 AND (instance_id < @beforeInstanceId OR instance_id >= 0))
                            OR (@beforeInstanceId > 0 AND instance_id >= 0 AND instance_id < @beforeInstanceId))
                        ORDER BY (instance_id < 0) DESC, instance_id DESC
                        LIMIT @limit", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
                    command.Parameters.AddWithValue("beforeInstanceId", NpgsqlDbType.Bigint, beforeInstanceId);
                    command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
                    return await ReadInstancesAsync(command, cancellationToken);
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                {
                    instances = read.Value;
                    return LeaderboardStoreResult.Success;
                }
            }
            catch
            {
            }
            return LeaderboardStoreResult.Failed;
        }

        public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot)
        {
            snapshot = new();
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            LeaderboardSnapshot committedSnapshot = new();
            LeaderboardStoreResult operationResult = LeaderboardStoreResult.Failed;
            try
            {
                PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("LeaderboardReconcile", 0, async (connection, transaction, cancellationToken) =>
                {
                    await LockReconciliationAsync(connection, transaction, cancellationToken);
                    await LockRequestedDefinitionsAsync(connection, transaction, request.DesiredDefinitions.Select(definition => definition.LeaderboardId), cancellationToken);
                    List<DBLeaderboard> currentDefinitions = await ReadDefinitionsAsync(connection, transaction, true, cancellationToken);
                    Dictionary<long, DBLeaderboard> currentById = currentDefinitions.ToDictionary(definition => definition.LeaderboardId);
                    List<DBLeaderboardInstance> currentInstances = await ReadRelevantInstancesForUpdateAsync(connection, transaction, currentDefinitions, currentById, request.DesiredDefinitions, cancellationToken);
                    if (TryValidateReconciliation(request, currentDefinitions, currentInstances, out Dictionary<long, LeaderboardDefinitionSpec> desiredDefinitions,
                        out Dictionary<long, LeaderboardInstanceSpec> initialInstances, out Dictionary<long, long> nextInstanceIds) == false
                        || HasActiveInstanceOwnershipMismatch(currentDefinitions, currentInstances))
                    {
                        operationResult = LeaderboardStoreResult.InvalidData;
                        Abort(LeaderboardStoreResult.InvalidData);
                    }

                    if (await HasGeneratedInstanceCollisionAsync(connection, transaction, nextInstanceIds.Values, cancellationToken))
                    {
                        operationResult = LeaderboardStoreResult.InvalidData;
                        Abort(LeaderboardStoreResult.InvalidData);
                    }

                    Dictionary<long, long> activeInstanceIds = new();
                    foreach ((long leaderboardId, LeaderboardDefinitionSpec definition) in desiredDefinitions)
                    {
                        if (currentById.TryGetValue(leaderboardId, out DBLeaderboard current) == false || (current.IsEnabled == false && definition.IsEnabled))
                            activeInstanceIds.Add(leaderboardId, nextInstanceIds[leaderboardId]);
                        else
                            activeInstanceIds.Add(leaderboardId, current.ActiveInstanceId);
                    }
                    if (TryValidateTopology(request.MetaMappings, desiredDefinitions.Keys, activeInstanceIds) == false)
                    {
                        operationResult = LeaderboardStoreResult.InvalidData;
                        Abort(LeaderboardStoreResult.InvalidData);
                    }

                    foreach (LeaderboardDefinitionSpec definition in desiredDefinitions.Values.OrderBy(definition => unchecked((ulong)definition.LeaderboardId)))
                    {
                        bool exists = currentById.TryGetValue(definition.LeaderboardId, out DBLeaderboard current);
                        bool createInitial = exists == false;
                        bool reenable = exists && current.IsEnabled == false && definition.IsEnabled;
                        long activeInstanceId = createInitial || reenable ? nextInstanceIds[definition.LeaderboardId] : current.ActiveInstanceId;
                        if (createInitial)
                            await InsertDefinitionAsync(connection, transaction, definition, activeInstanceId, cancellationToken);
                        else
                            await UpdateDefinitionAsync(connection, transaction, definition, activeInstanceId, cancellationToken);

                        if (createInitial || reenable)
                            await InsertInstanceAsync(connection, transaction, activeInstanceId, definition, initialInstances[definition.LeaderboardId], request.CurrentTime, cancellationToken);
                        else if (definition.IsEnabled == false)
                            await TerminalizeInstanceAsync(connection, transaction, current.LeaderboardId, current.ActiveInstanceId, cancellationToken);
                        else
                            await RepairActivationDateAsync(connection, transaction, definition.LeaderboardId, initialInstances[definition.LeaderboardId], request.CurrentTime, cancellationToken);
                    }

                    foreach (DBLeaderboard definition in currentDefinitions.Where(definition => desiredDefinitions.ContainsKey(definition.LeaderboardId) == false))
                    {
                        await SetDefinitionDisabledAsync(connection, transaction, definition.LeaderboardId, cancellationToken);
                        await TerminalizeInstanceAsync(connection, transaction, definition.LeaderboardId, definition.ActiveInstanceId, cancellationToken);
                    }

                    foreach (long leaderboardId in desiredDefinitions.Keys)
                        await DeleteActiveMappingsAsync(connection, transaction, leaderboardId, activeInstanceIds[leaderboardId], cancellationToken);
                    foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                        await InsertMappingAsync(connection, transaction, mapping, cancellationToken);

                    committedSnapshot = await ReadSnapshotAsync(connection, transaction, desiredDefinitions.Keys, request.NormalArchiveLimit, cancellationToken);
                }).GetAwaiter().GetResult();
                if (write.Outcome == PostgreSQLWriteOutcome.Success)
                {
                    snapshot = committedSnapshot;
                    return LeaderboardStoreResult.Success;
                }
                return write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain ? LeaderboardStoreResult.OutcomeUncertain : operationResult;
            }
            catch (LeaderboardWriteAbortedException exception)
            {
                return exception.Result;
            }
            catch
            {
                return LeaderboardStoreResult.Failed;
            }
        }

        public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request)
        {
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            return ExecuteLifecycleWrite("LeaderboardActivate", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, [request.LeaderboardId], cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance instance = await ReadInstanceAsync(connection, transaction, request.InstanceId, true, cancellationToken);
                if (instance == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (instance.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);
                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId || request.InstanceId != definition.ActiveInstanceId)
                    abort(LeaderboardStoreResult.StaleState);
                if (instance.State == LeaderboardState.eLBS_Active)
                    return;
                if (instance.State != request.ExpectedState)
                    abort(LeaderboardStoreResult.StaleState);
                if (await UpdateInstanceStateAsync(connection, transaction, request.InstanceId, request.ExpectedState, LeaderboardState.eLBS_Active, cancellationToken) == false)
                    abort(LeaderboardStoreResult.StaleState);
            });
        }

        public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request)
        {
            if (request == null || request.Entries.GroupBy(entry => entry.ParticipantId).Any(group => group.Skip(1).Any()))
                return LeaderboardStoreResult.InvalidData;

            return ExecuteLifecycleWrite("LeaderboardSaveScores", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, [request.LeaderboardId], cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance instance = await ReadInstanceAsync(connection, transaction, request.InstanceId, true, cancellationToken);
                if (instance == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (instance.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);
                if (definition.ActiveInstanceId != request.InstanceId || instance.State != request.ExpectedState)
                    abort(LeaderboardStoreResult.StaleState);
                await UpsertEntriesAsync(connection, transaction, request.InstanceId, request.Entries, true, cancellationToken);
            });
        }

        public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request)
        {
            if (request == null || request.Entries.GroupBy(entry => entry.ParticipantId).Any(group => group.Skip(1).Any()))
                return LeaderboardStoreResult.InvalidData;

            return ExecuteLifecycleWrite("LeaderboardExpire", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, [request.LeaderboardId], cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance instance = await ReadInstanceAsync(connection, transaction, request.InstanceId, true, cancellationToken);
                if (instance == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (instance.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);

                List<DBLeaderboardEntry> existingEntries = await ReadEntriesForUpdateAsync(connection, transaction, request.InstanceId, cancellationToken);
                if (instance.State == LeaderboardState.eLBS_Expired)
                {
                    if (HasExactEntries(existingEntries, request.Entries) == false)
                        abort(LeaderboardStoreResult.Conflict);
                    return;
                }
                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId || request.InstanceId != definition.ActiveInstanceId
                    || instance.State != request.ExpectedState)
                    abort(LeaderboardStoreResult.StaleState);
                if (existingEntries.Any(existing => request.Entries.Any(requested => requested.ParticipantId == existing.ParticipantId && EntriesMatch(existing, requested)) == false))
                    abort(LeaderboardStoreResult.Conflict);

                await UpsertEntriesAsync(connection, transaction, request.InstanceId,
                    request.Entries.Where(requested => existingEntries.All(existing => existing.ParticipantId != requested.ParticipantId)).ToArray(), false, cancellationToken);
                if (await UpdateInstanceStateAsync(connection, transaction, request.InstanceId, request.ExpectedState, LeaderboardState.eLBS_Expired, cancellationToken) == false)
                    abort(LeaderboardStoreResult.StaleState);
            });
        }

        public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance)
        {
            committedInstance = null;
            if (request == null)
                return LeaderboardStoreResult.InvalidData;

            DBLeaderboardInstance committed = null;
            LeaderboardStoreResult result = ExecuteLifecycleWrite("LeaderboardRotate", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, request.MetaMappings.Select(mapping => mapping.SubLeaderboardId).Append(request.LeaderboardId), cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance previous = await ReadInstanceAsync(connection, transaction, request.ExpectedActiveInstanceId, true, cancellationToken);
                if (previous == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (previous.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);

                if (definition.ActiveInstanceId != request.ExpectedActiveInstanceId)
                {
                    if (TryGetReplayInstanceId(request, out long replayInstanceId) == false || definition.ActiveInstanceId != replayInstanceId)
                        abort(LeaderboardStoreResult.StaleState);
                    DBLeaderboardInstance replay = await ReadInstanceAsync(connection, transaction, replayInstanceId, true, cancellationToken);
                    if (replay == null || replay.LeaderboardId != request.LeaderboardId || previous.State != request.PreviousState
                        || MatchesInstance(replay, request.NextInstance) == false)
                        abort(LeaderboardStoreResult.Conflict);
                    if (HasExactMappings(await ReadMappingsAsync(connection, transaction, request.LeaderboardId, replayInstanceId, cancellationToken), request.MetaMappings, replayInstanceId) == false)
                        abort(LeaderboardStoreResult.Conflict);
                    committed = CloneInstance(replay);
                    return;
                }
                if (previous.State != request.ExpectedActiveState)
                    abort(LeaderboardStoreResult.StaleState);

                List<long> instanceIds = await ReadInstanceIdsForUpdateAsync(connection, transaction, request.LeaderboardId, cancellationToken);
                if (TryGetReplayInstanceId(request, out long generatedInstanceId) == false || LeaderboardInstanceIdGenerator.TryGetNext(request.LeaderboardId, instanceIds, out long actualInstanceId) == false
                    || generatedInstanceId != actualInstanceId || await InstanceExistsAsync(connection, transaction, generatedInstanceId, cancellationToken)
                    || (request.NextInstance.InstanceId != 0 && request.NextInstance.InstanceId != generatedInstanceId))
                    abort(LeaderboardStoreResult.InvalidData);

                foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                {
                    DBLeaderboard subDefinition = await ReadDefinitionAsync(connection, transaction, mapping.SubLeaderboardId, true, cancellationToken);
                    DBLeaderboardInstance subInstance = await ReadInstanceAsync(connection, transaction, mapping.SubInstanceId, true, cancellationToken);
                    if (subDefinition == null || subInstance == null || subDefinition.ActiveInstanceId != mapping.SubInstanceId
                        || subInstance.LeaderboardId != mapping.SubLeaderboardId)
                        abort(LeaderboardStoreResult.InvalidData);
                }

                await InsertLifecycleInstanceAsync(connection, transaction, generatedInstanceId, request.NextInstance, cancellationToken);
                if (await UpdateInstanceStateAsync(connection, transaction, request.ExpectedActiveInstanceId, request.ExpectedActiveState, request.PreviousState, cancellationToken) == false
                    || await UpdateActiveInstanceAsync(connection, transaction, request.LeaderboardId, request.ExpectedActiveInstanceId, generatedInstanceId, cancellationToken) == false)
                    abort(LeaderboardStoreResult.StaleState);
                foreach (LeaderboardMetaMapping mapping in request.MetaMappings)
                    await InsertMappingAsync(connection, transaction, new LeaderboardMetaMapping(request.LeaderboardId, generatedInstanceId, mapping.SubLeaderboardId, mapping.SubInstanceId), cancellationToken);
                committed = new DBLeaderboardInstance
                {
                    InstanceId = generatedInstanceId,
                    LeaderboardId = request.LeaderboardId,
                    State = request.NextState,
                    ActivationDate = request.NextInstance.ActivationDate,
                    Visible = request.NextInstance.Visible,
                };
            });
            if (result == LeaderboardStoreResult.Success)
                committedInstance = CloneInstance(committed);
            return result;
        }
        public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot)
        {
            snapshot = new();
            if (request == null || request.ArchiveLimit < 0)
                return LeaderboardStoreResult.InvalidData;

            LeaderboardVisibilitySnapshot committedSnapshot = new();
            LeaderboardStoreResult result = ExecuteLifecycleWrite("LeaderboardMaintainVisibility", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, [request.LeaderboardId], cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance active = await ReadInstanceAsync(connection, transaction, definition.ActiveInstanceId, true, cancellationToken);
                if (active == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (active.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);

                List<DBLeaderboardInstance> archives = await ReadTerminalInstancesForUpdateAsync(connection, transaction, request.LeaderboardId, cancellationToken);
                HashSet<long> rewardBearingIds = await ReadRewardBearingInstanceIdsAsync(connection, transaction, request.LeaderboardId, cancellationToken);
                HashSet<long> entryBearingIds = await ReadEntryBearingInstanceIdsAsync(connection, transaction, archives.Select(archive => archive.InstanceId), cancellationToken);
                List<DBLeaderboardInstance> normalArchives = archives
                    .Where(archive => entryBearingIds.Contains(archive.InstanceId) && rewardBearingIds.Contains(archive.InstanceId) == false)
                    .OrderByDescending(archive => unchecked((ulong)archive.InstanceId))
                    .Take(request.ArchiveLimit)
                    .ToList();
                HashSet<long> normalArchiveIds = normalArchives.Select(archive => archive.InstanceId).ToHashSet();
                List<DBLeaderboardInstance> changedInstances = new();

                foreach (DBLeaderboardInstance archive in archives)
                {
                    bool visible = rewardBearingIds.Contains(archive.InstanceId) || normalArchiveIds.Contains(archive.InstanceId);
                    if (archive.Visible == visible)
                        continue;

                    await UpdateInstanceVisibilityAsync(connection, transaction, archive.InstanceId, visible, cancellationToken);
                    archive.Visible = visible;
                    changedInstances.Add(archive);
                }

                List<DBMetaEntry> mappings = await ReadMappingsForInstancesAsync(connection, transaction, request.LeaderboardId, normalArchiveIds, cancellationToken);
                committedSnapshot = new(normalArchives, changedInstances, mappings);
            });
            if (result == LeaderboardStoreResult.Success)
                snapshot = committedSnapshot;
            return result;
        }

        public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request)
        {
            if (request == null || request.Rewards == null || request.Rewards.GroupBy(reward => reward.ParticipantId).Any(group => group.Skip(1).Any())
                || request.Rewards.Any(reward => reward.LeaderboardId != request.LeaderboardId || reward.InstanceId != request.InstanceId || reward.Rank <= 0))
                return LeaderboardStoreResult.InvalidData;

            return ExecuteLifecycleWrite("LeaderboardGenerateRewards", request.LeaderboardId, async (connection, transaction, cancellationToken, abort) =>
            {
                await LockRequestedDefinitionsAsync(connection, transaction, [request.LeaderboardId], cancellationToken);
                DBLeaderboard definition = await ReadDefinitionAsync(connection, transaction, request.LeaderboardId, true, cancellationToken);
                if (definition == null)
                    abort(LeaderboardStoreResult.NotFound);
                DBLeaderboardInstance instance = await ReadInstanceAsync(connection, transaction, request.InstanceId, true, cancellationToken);
                if (instance == null)
                    abort(LeaderboardStoreResult.NotFound);
                if (instance.LeaderboardId != request.LeaderboardId)
                    abort(LeaderboardStoreResult.InvalidData);

                List<DBRewardEntry> existingRewards = await ReadRewardsForUpdateAsync(connection, transaction, request.LeaderboardId, request.InstanceId, cancellationToken);
                if (instance.State == LeaderboardState.eLBS_Rewarded)
                {
                    if (HasExactRewards(existingRewards, request.Rewards) == false)
                        abort(LeaderboardStoreResult.Conflict);
                    return;
                }
                if (instance.State != request.ExpectedState)
                    abort(LeaderboardStoreResult.StaleState);
                if (existingRewards.Count != 0)
                    abort(LeaderboardStoreResult.Conflict);

                await InsertRewardsAsync(connection, transaction, request.Rewards, cancellationToken);
                if (await UpdateInstanceStateAsync(connection, transaction, request.InstanceId, request.ExpectedState, LeaderboardState.eLBS_Rewarded, cancellationToken) == false)
                    abort(LeaderboardStoreResult.StaleState);
            });
        }

        public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards)
        {
            rewards = Array.Empty<DBRewardEntry>();
            try
            {
                PostgreSQLReadResult<IReadOnlyList<DBRewardEntry>> read = _executor.ExecuteReadAsync("LeaderboardGetPendingRewards", async (connection, deadline, cancellationToken) =>
                {
                    await using NpgsqlCommand command = new($@"SELECT leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date, rewarded_date
                        FROM {RewardTable}
                        WHERE participant_id = @participantId AND (rewarded_date IS NULL OR rewarded_date = 0)
                        ORDER BY leaderboard_id, instance_id", connection)
                    {
                        CommandTimeout = deadline.RemainingCommandTimeoutSeconds,
                    };
                    command.Parameters.AddWithValue("participantId", NpgsqlDbType.Bigint, participantId);
                    return await ReadRewardsAsync(command, cancellationToken);
                }).GetAwaiter().GetResult();
                if (read.Succeeded)
                {
                    rewards = read.Value;
                    return LeaderboardStoreResult.Success;
                }
            }
            catch
            {
            }
            return LeaderboardStoreResult.Failed;
        }

        public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate)
        {
            if (rewardedDate == 0)
                return RewardFinalizationResult.Failed;

            RewardFinalizationResult operationResult = RewardFinalizationResult.Failed;
            try
            {
                PostgreSQLWriteResult write = _executor.ExecuteWriteAsync("LeaderboardFinalizeReward", key.LeaderboardId, async (connection, transaction, cancellationToken) =>
                {
                    await using NpgsqlCommand update = new($@"UPDATE {RewardTable} SET rewarded_date = @rewardedDate
                        WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId AND participant_id = @participantId
                          AND (rewarded_date IS NULL OR rewarded_date = 0)", connection, transaction);
                    update.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, key.LeaderboardId);
                    update.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, key.InstanceId);
                    update.Parameters.AddWithValue("participantId", NpgsqlDbType.Bigint, key.ParticipantId);
                    update.Parameters.AddWithValue("rewardedDate", NpgsqlDbType.Bigint, rewardedDate);
                    if (await update.ExecuteNonQueryAsync(cancellationToken) == 1)
                    {
                        operationResult = RewardFinalizationResult.Finalized;
                    }
                    else
                    {
                        await using NpgsqlCommand existing = new($"SELECT rewarded_date FROM {RewardTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId AND participant_id = @participantId", connection, transaction);
                        existing.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, key.LeaderboardId);
                        existing.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, key.InstanceId);
                        existing.Parameters.AddWithValue("participantId", NpgsqlDbType.Bigint, key.ParticipantId);
                        operationResult = await existing.ExecuteScalarAsync(cancellationToken) == null
                            ? RewardFinalizationResult.NotFound
                            : RewardFinalizationResult.AlreadyFinalized;
                    }
                    if (LifecyclePreCommitHook.Value != null)
                        await LifecyclePreCommitHook.Value(connection, transaction);
                }, notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();
                return write.Outcome == PostgreSQLWriteOutcome.Success
                    ? operationResult
                    : write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain ? RewardFinalizationResult.OutcomeUncertain : RewardFinalizationResult.Failed;
            }
            catch
            {
                return RewardFinalizationResult.Failed;
            }
        }

        private LeaderboardStoreResult ExecuteLifecycleWrite(string operation, long leaderboardId,
            Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Action<LeaderboardStoreResult>, Task> writeAsync)
        {
            LeaderboardStoreResult operationResult = LeaderboardStoreResult.Failed;
            try
            {
                PostgreSQLWriteResult write = _executor.ExecuteWriteAsync(operation, leaderboardId, (connection, transaction, cancellationToken) =>
                    InvokeLifecycleWriteAsync(connection, transaction, cancellationToken, writeAsync, result =>
                    {
                        operationResult = result;
                        Abort(result);
                    }), notifyFatalOnOutcomeUncertain: true).GetAwaiter().GetResult();
                return write.Outcome == PostgreSQLWriteOutcome.Success
                    ? LeaderboardStoreResult.Success
                    : write.Outcome == PostgreSQLWriteOutcome.OutcomeUncertain ? LeaderboardStoreResult.OutcomeUncertain : operationResult;
            }
            catch
            {
                return LeaderboardStoreResult.Failed;
            }
        }

        private static async Task InvokeLifecycleWriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken,
            Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Action<LeaderboardStoreResult>, Task> writeAsync, Action<LeaderboardStoreResult> abort)
        {
            await writeAsync(connection, transaction, cancellationToken, abort);
            if (LifecyclePreCommitHook.Value != null)
                await LifecyclePreCommitHook.Value(connection, transaction);
        }

        private static async Task<DBLeaderboard> ReadDefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, bool forUpdate, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count FROM {DefinitionTable} WHERE leaderboard_id = @leaderboardId{(forUpdate ? " FOR UPDATE" : string.Empty)}", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new DBLeaderboard { LeaderboardId = reader.GetInt64(0), PrototypeName = reader.GetString(1), ActiveInstanceId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2), IsEnabled = reader.GetBoolean(3), StartTime = reader.GetInt64(4), MaxResetCount = reader.GetInt32(5) }
                : null;
        }

        private static async Task<DBLeaderboardInstance> ReadInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, bool forUpdate, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT instance_id, leaderboard_id, state, activation_date, visible FROM {InstanceTable} WHERE instance_id = @instanceId{(forUpdate ? " FOR UPDATE" : string.Empty)}", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? ReadInstance(reader) : null;
        }

        private static async Task<List<DBLeaderboardEntry>> ReadEntriesForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT instance_id, participant_id, score, high_score, rule_states FROM {EntryTable} WHERE instance_id = @instanceId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            return (await ReadEntriesAsync(command, cancellationToken)).ToList();
        }

        private static async Task<List<DBLeaderboardInstance>> ReadTerminalInstancesForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            long leaderboardId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT instance_id, leaderboard_id, state, activation_date, visible FROM {InstanceTable} WHERE leaderboard_id = @leaderboardId AND state >= @terminalState FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("terminalState", NpgsqlDbType.Smallint, (short)LeaderboardState.eLBS_Rewarded);
            return (await ReadInstancesAsync(command, cancellationToken)).ToList();
        }

        private static async Task<HashSet<long>> ReadRewardBearingInstanceIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            long leaderboardId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT DISTINCT instance_id FROM {RewardTable} WHERE leaderboard_id = @leaderboardId", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            HashSet<long> instanceIds = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                instanceIds.Add(reader.GetInt64(0));
            return instanceIds;
        }

        private static async Task<HashSet<long>> ReadEntryBearingInstanceIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            IEnumerable<long> instanceIds, CancellationToken cancellationToken)
        {
            long[] ids = instanceIds.ToArray();
            if (ids.Length == 0)
                return new();

            await using NpgsqlCommand command = new($"SELECT DISTINCT instance_id FROM {EntryTable} WHERE instance_id = ANY(@instanceIds)", connection, transaction);
            command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            HashSet<long> result = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(reader.GetInt64(0));
            return result;
        }

        private static async Task UpdateInstanceVisibilityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, bool visible,
            CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {InstanceTable} SET visible = @visible WHERE instance_id = @instanceId", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            command.Parameters.AddWithValue("visible", NpgsqlDbType.Boolean, visible);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<List<DBMetaEntry>> ReadMappingsForInstancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId,
            IEnumerable<long> instanceIds, CancellationToken cancellationToken)
        {
            long[] ids = instanceIds.ToArray();
            if (ids.Length == 0)
                return new();

            await using NpgsqlCommand command = new($@"SELECT leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id FROM {MetaEntryTable}
                WHERE leaderboard_id = @leaderboardId AND instance_id = ANY(@instanceIds)
                ORDER BY instance_id, sub_leaderboard_id FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            List<DBMetaEntry> mappings = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                mappings.Add(new DBMetaEntry { LeaderboardId = reader.GetInt64(0), InstanceId = reader.GetInt64(1), SubLeaderboardId = reader.GetInt64(2), SubInstanceId = reader.GetInt64(3) });
            return mappings;
        }

        private static async Task<List<DBRewardEntry>> ReadRewardsForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId,
            long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($@"SELECT leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date, rewarded_date
                FROM {RewardTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            return (await ReadRewardsAsync(command, cancellationToken)).ToList();
        }

        private static async Task InsertRewardsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<LeaderboardRewardWrite> rewards,
            CancellationToken cancellationToken)
        {
            if (rewards.Count == 0)
                return;

            await using NpgsqlCommand command = new($@"INSERT INTO {RewardTable} (leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date, rewarded_date)
                SELECT input.leaderboard_id, input.instance_id, input.participant_id, input.reward_id, input.rank, input.creation_date, NULL
                FROM unnest(@leaderboardIds, @instanceIds, @participantIds, @rewardIds, @ranks, @creationDates)
                    AS input(leaderboard_id, instance_id, participant_id, reward_id, rank, creation_date)", connection, transaction);
            command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rewards.Select(reward => reward.LeaderboardId).ToArray());
            command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rewards.Select(reward => reward.InstanceId).ToArray());
            command.Parameters.AddWithValue("participantIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rewards.Select(reward => reward.ParticipantId).ToArray());
            command.Parameters.AddWithValue("rewardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rewards.Select(reward => reward.RewardId).ToArray());
            command.Parameters.AddWithValue("ranks", NpgsqlDbType.Array | NpgsqlDbType.Integer, rewards.Select(reward => reward.Rank).ToArray());
            command.Parameters.AddWithValue("creationDates", NpgsqlDbType.Array | NpgsqlDbType.Bigint, rewards.Select(reward => reward.CreationDate).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<List<DBMetaEntry>> ReadMappingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id FROM {MetaEntryTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            List<DBMetaEntry> mappings = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                mappings.Add(new DBMetaEntry { LeaderboardId = reader.GetInt64(0), InstanceId = reader.GetInt64(1), SubLeaderboardId = reader.GetInt64(2), SubInstanceId = reader.GetInt64(3) });
            return mappings;
        }

        private static async Task<List<long>> ReadInstanceIdsForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT instance_id FROM {InstanceTable} WHERE leaderboard_id = @leaderboardId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            List<long> ids = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetInt64(0));
            return ids;
        }

        private static async Task<bool> InstanceExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT 1 FROM {InstanceTable} WHERE instance_id = @instanceId FOR UPDATE", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        private static async Task<bool> UpdateInstanceStateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, LeaderboardState expectedState, LeaderboardState state, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {InstanceTable} SET state = @state WHERE instance_id = @instanceId AND state = @expectedState", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            command.Parameters.AddWithValue("expectedState", NpgsqlDbType.Smallint, (short)expectedState);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Smallint, (short)state);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        private static async Task<bool> UpdateActiveInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, long expectedInstanceId, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {DefinitionTable} SET active_instance_id = @instanceId WHERE leaderboard_id = @leaderboardId AND active_instance_id = @expectedInstanceId", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("expectedInstanceId", NpgsqlDbType.Bigint, expectedInstanceId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }

        private static async Task InsertLifecycleInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, LeaderboardInstanceSpec instance, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"INSERT INTO {InstanceTable} (instance_id, leaderboard_id, state, activation_date, visible) VALUES (@instanceId, @leaderboardId, @state, @activationDate, @visible)", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, instance.LeaderboardId);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Smallint, (short)instance.State);
            command.Parameters.AddWithValue("activationDate", NpgsqlDbType.Bigint, instance.ActivationDate);
            command.Parameters.AddWithValue("visible", NpgsqlDbType.Boolean, instance.Visible);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task UpsertEntriesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, IReadOnlyList<LeaderboardEntryWrite> entries, bool updateExisting, CancellationToken cancellationToken)
        {
            if (entries.Count == 0)
                return;

            await using NpgsqlCommand command = new($@"INSERT INTO {EntryTable} (instance_id, participant_id, score, high_score, rule_states)
                SELECT @instanceId, input.participant_id, input.score, input.high_score, input.rule_states
                FROM unnest(@participantIds, @scores, @highScores, @ruleStates) AS input(participant_id, score, high_score, rule_states)
                ON CONFLICT (instance_id, participant_id) DO {(updateExisting ? "UPDATE SET score = EXCLUDED.score, high_score = EXCLUDED.high_score, rule_states = EXCLUDED.rule_states" : "NOTHING")}", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            command.Parameters.AddWithValue("participantIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entries.Select(entry => entry.ParticipantId).ToArray());
            command.Parameters.AddWithValue("scores", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entries.Select(entry => entry.Score).ToArray());
            command.Parameters.AddWithValue("highScores", NpgsqlDbType.Array | NpgsqlDbType.Bigint, entries.Select(entry => entry.HighScore).ToArray());
            command.Parameters.AddWithValue("ruleStates", NpgsqlDbType.Array | NpgsqlDbType.Bytea, entries.Select(entry => entry.RuleStates).ToArray());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static bool TryGetReplayInstanceId(LeaderboardRotation request, out long instanceId)
        {
            if (request.NextInstance.InstanceId != 0)
            {
                instanceId = request.NextInstance.InstanceId;
                return true;
            }
            return LeaderboardInstanceIdGenerator.TryGetNext(request.LeaderboardId, [request.ExpectedActiveInstanceId], out instanceId);
        }

        private static bool HasExactEntries(IReadOnlyCollection<DBLeaderboardEntry> existingEntries, IReadOnlyList<LeaderboardEntryWrite> requestedEntries)
        {
            return existingEntries.Count == requestedEntries.Count
                && requestedEntries.All(requested => existingEntries.Any(existing => existing.ParticipantId == requested.ParticipantId && EntriesMatch(existing, requested)));
        }

        private static bool HasExactRewards(IReadOnlyCollection<DBRewardEntry> existingRewards, IReadOnlyList<LeaderboardRewardWrite> requestedRewards)
        {
            return existingRewards.Count == requestedRewards.Count
                && requestedRewards.All(requested => existingRewards.Any(existing => existing.ParticipantId == requested.ParticipantId
                    && existing.RewardId == requested.RewardId && existing.Rank == requested.Rank));
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

        private static DBLeaderboardInstance CloneInstance(DBLeaderboardInstance instance)
        {
            return instance == null ? null : new DBLeaderboardInstance
            {
                InstanceId = instance.InstanceId,
                LeaderboardId = instance.LeaderboardId,
                State = instance.State,
                ActivationDate = instance.ActivationDate,
                Visible = instance.Visible,
            };
        }

        private static void Abort(LeaderboardStoreResult result)
        {
            throw new LeaderboardWriteAbortedException(result);
        }

        private static bool TryValidateReconciliation(LeaderboardReconciliation request, IReadOnlyList<DBLeaderboard> currentDefinitions,
            IReadOnlyList<DBLeaderboardInstance> currentInstances, out Dictionary<long, LeaderboardDefinitionSpec> desiredDefinitions,
            out Dictionary<long, LeaderboardInstanceSpec> initialInstances, out Dictionary<long, long> nextInstanceIds)
        {
            desiredDefinitions = null;
            initialInstances = null;
            nextInstanceIds = null;
            if (request.NormalArchiveLimit < 0 || request.DesiredDefinitions.Any(definition => definition.MaxResetCount < 0)
                || request.DesiredDefinitions.GroupBy(definition => definition.LeaderboardId).Any(group => group.Skip(1).Any())
                || request.DesiredDefinitions.GroupBy(definition => definition.PrototypeName, StringComparer.Ordinal).Any(group => group.Skip(1).Any()))
                return false;

            Dictionary<long, LeaderboardDefinitionSpec> requestedDefinitions = request.DesiredDefinitions.ToDictionary(definition => definition.LeaderboardId);
            if (request.InitialInstances.GroupBy(instance => instance.LeaderboardId).Any(group => group.Skip(1).Any())
                || request.InitialInstances.Any(instance => requestedDefinitions.ContainsKey(instance.LeaderboardId) == false)
                || requestedDefinitions.Keys.Any(leaderboardId => request.InitialInstances.Count(instance => instance.LeaderboardId == leaderboardId) != 1))
                return false;

            Dictionary<long, LeaderboardInstanceSpec> requestedInitialInstances = request.InitialInstances.ToDictionary(instance => instance.LeaderboardId);
            if (requestedInitialInstances.Any(pair => pair.Value.State != (requestedDefinitions[pair.Key].IsEnabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded)
                || pair.Value.Visible != requestedDefinitions[pair.Key].IsEnabled))
                return false;

            Dictionary<long, DBLeaderboard> currentById = currentDefinitions.ToDictionary(definition => definition.LeaderboardId);
            HashSet<long> existingIds = currentInstances.Select(instance => instance.InstanceId).ToHashSet();
            HashSet<long> generatedIds = new();
            Dictionary<long, long> generatedInstanceIds = new();
            foreach (LeaderboardDefinitionSpec definition in requestedDefinitions.Values.OrderBy(definition => unchecked((ulong)definition.LeaderboardId)))
            {
                bool needsNewInstance = currentById.TryGetValue(definition.LeaderboardId, out DBLeaderboard current) == false || (current.IsEnabled == false && definition.IsEnabled);
                if (needsNewInstance == false)
                    continue;
                IEnumerable<long> leaderboardInstances = currentInstances.Where(instance => instance.LeaderboardId == definition.LeaderboardId).Select(instance => instance.InstanceId);
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

        private static bool TryValidateTopology(IReadOnlyList<LeaderboardMetaMapping> mappings, IEnumerable<long> desiredLeaderboardIds, IReadOnlyDictionary<long, long> activeInstanceIds)
        {
            HashSet<long> desired = desiredLeaderboardIds.ToHashSet();
            return mappings.GroupBy(mapping => (mapping.LeaderboardId, mapping.InstanceId, mapping.SubLeaderboardId)).All(group => group.Count() == 1)
                && mappings.All(mapping => desired.Contains(mapping.LeaderboardId) && desired.Contains(mapping.SubLeaderboardId)
                    && activeInstanceIds[mapping.LeaderboardId] == mapping.InstanceId && activeInstanceIds[mapping.SubLeaderboardId] == mapping.SubInstanceId);
        }

        private static bool HasActiveInstanceOwnershipMismatch(IEnumerable<DBLeaderboard> definitions, IReadOnlyList<DBLeaderboardInstance> instances)
        {
            return definitions.Any(definition => instances.Any(instance => instance.LeaderboardId == definition.LeaderboardId && instance.InstanceId == definition.ActiveInstanceId) == false);
        }

        private static async Task LockRequestedDefinitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<long> leaderboardIds, CancellationToken cancellationToken)
        {
            foreach (long leaderboardId in leaderboardIds.Distinct().OrderBy(leaderboardId => unchecked((ulong)leaderboardId)))
            {
                await using NpgsqlCommand command = new("SELECT pg_advisory_xact_lock(@leaderboardId)", connection, transaction);
                command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        private static async Task LockReconciliationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new("SELECT pg_advisory_xact_lock(@lockKey)", connection, transaction);
            command.Parameters.AddWithValue("lockKey", NpgsqlDbType.Bigint, ReconciliationLockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<List<DBLeaderboard>> ReadDefinitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, bool forUpdate, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count FROM {DefinitionTable} ORDER BY (leaderboard_id < 0), leaderboard_id{(forUpdate ? " FOR UPDATE" : string.Empty)}", connection, transaction);
            List<DBLeaderboard> definitions = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                definitions.Add(new DBLeaderboard { LeaderboardId = reader.GetInt64(0), PrototypeName = reader.GetString(1), ActiveInstanceId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2), IsEnabled = reader.GetBoolean(3), StartTime = reader.GetInt64(4), MaxResetCount = reader.GetInt32(5) });
            return definitions;
        }

        private static async Task<List<DBLeaderboardInstance>> ReadRelevantInstancesForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            IReadOnlyList<DBLeaderboard> currentDefinitions, IReadOnlyDictionary<long, DBLeaderboard> currentById,
            IReadOnlyList<LeaderboardDefinitionSpec> desiredDefinitions, CancellationToken cancellationToken)
        {
            Dictionary<long, DBLeaderboardInstance> instances = new();
            List<DBLeaderboard> definitionsWithActiveInstances = currentDefinitions.Where(definition => definition.ActiveInstanceId != 0).ToList();
            if (definitionsWithActiveInstances.Count > 0)
            {
                await using NpgsqlCommand command = new($@"SELECT instance.instance_id, instance.leaderboard_id, instance.state, instance.activation_date, instance.visible
                    FROM {InstanceTable} instance
                    INNER JOIN unnest(@leaderboardIds, @instanceIds) requested(leaderboard_id, instance_id)
                        ON requested.leaderboard_id = instance.leaderboard_id AND requested.instance_id = instance.instance_id
                    ORDER BY (instance.instance_id < 0), instance.instance_id FOR UPDATE", connection, transaction);
                command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, definitionsWithActiveInstances.Select(definition => definition.LeaderboardId).ToArray());
                command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, definitionsWithActiveInstances.Select(definition => definition.ActiveInstanceId).ToArray());
                foreach (DBLeaderboardInstance instance in await ReadInstancesAsync(command, cancellationToken))
                    instances.TryAdd(instance.InstanceId, instance);
            }

            long[] reenabledIds = desiredDefinitions.Where(definition => currentById.TryGetValue(definition.LeaderboardId, out DBLeaderboard current)
                && current.IsEnabled == false && definition.IsEnabled).Select(definition => definition.LeaderboardId).Distinct().OrderBy(id => unchecked((ulong)id)).ToArray();
            if (reenabledIds.Length > 0)
            {
                await using NpgsqlCommand command = new($"SELECT instance_id, leaderboard_id, state, activation_date, visible FROM {InstanceTable} WHERE leaderboard_id = ANY(@leaderboardIds) ORDER BY (instance_id < 0), instance_id FOR UPDATE", connection, transaction);
                command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, reenabledIds);
                foreach (DBLeaderboardInstance instance in await ReadInstancesAsync(command, cancellationToken))
                    instances.TryAdd(instance.InstanceId, instance);
            }
            return instances.Values.ToList();
        }

        private static async Task<bool> HasGeneratedInstanceCollisionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<long> instanceIds, CancellationToken cancellationToken)
        {
            long[] ids = instanceIds.ToArray();
            if (ids.Length == 0)
                return false;
            await using NpgsqlCommand command = new($"SELECT 1 FROM {InstanceTable} WHERE instance_id = ANY(@instanceIds) LIMIT 1", connection, transaction);
            command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            return await command.ExecuteScalarAsync(cancellationToken) != null;
        }

        private static async Task<IReadOnlyList<DBLeaderboardEntry>> ReadEntriesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            List<DBLeaderboardEntry> entries = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new DBLeaderboardEntry { InstanceId = reader.GetInt64(0), ParticipantId = reader.GetInt64(1), Score = reader.GetInt64(2), HighScore = reader.GetInt64(3), RuleStates = reader.GetFieldValue<byte[]>(4).ToArray() });
            return entries;
        }

        private static async Task<IReadOnlyList<DBRewardEntry>> ReadRewardsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            List<DBRewardEntry> rewards = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rewards.Add(new DBRewardEntry
                {
                    LeaderboardId = reader.GetInt64(0),
                    InstanceId = reader.GetInt64(1),
                    ParticipantId = reader.GetInt64(2),
                    RewardId = reader.GetInt64(3),
                    Rank = reader.GetInt32(4),
                    CreationDate = reader.GetInt64(5),
                    RewardedDate = reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                });
            }
            return rewards;
        }

        private static async Task<IReadOnlyList<DBLeaderboardInstance>> ReadInstancesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
        {
            List<DBLeaderboardInstance> instances = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                instances.Add(ReadInstance(reader));
            return instances;
        }

        private static DBLeaderboardInstance ReadInstance(NpgsqlDataReader reader)
        {
            return new() { InstanceId = reader.GetInt64(0), LeaderboardId = reader.GetInt64(1), State = (LeaderboardState)reader.GetInt16(2), ActivationDate = reader.GetInt64(3), Visible = reader.GetBoolean(4) };
        }

        private static async Task InsertDefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaderboardDefinitionSpec definition, long activeInstanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"INSERT INTO {DefinitionTable} (leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count) VALUES (@leaderboardId, @prototypeName, @activeInstanceId, @isEnabled, @startTime, @maxResetCount)", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, definition.LeaderboardId);
            command.Parameters.AddWithValue("prototypeName", NpgsqlDbType.Text, definition.PrototypeName);
            command.Parameters.AddWithValue("activeInstanceId", NpgsqlDbType.Bigint, activeInstanceId);
            command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, definition.IsEnabled);
            command.Parameters.AddWithValue("startTime", NpgsqlDbType.Bigint, definition.StartTime);
            command.Parameters.AddWithValue("maxResetCount", NpgsqlDbType.Integer, definition.MaxResetCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task UpdateDefinitionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaderboardDefinitionSpec definition, long activeInstanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {DefinitionTable} SET prototype_name = @prototypeName, active_instance_id = @activeInstanceId, is_enabled = @isEnabled, start_time = @startTime, max_reset_count = @maxResetCount WHERE leaderboard_id = @leaderboardId", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, definition.LeaderboardId);
            command.Parameters.AddWithValue("prototypeName", NpgsqlDbType.Text, definition.PrototypeName);
            command.Parameters.AddWithValue("activeInstanceId", NpgsqlDbType.Bigint, activeInstanceId);
            command.Parameters.AddWithValue("isEnabled", NpgsqlDbType.Boolean, definition.IsEnabled);
            command.Parameters.AddWithValue("startTime", NpgsqlDbType.Bigint, definition.StartTime);
            command.Parameters.AddWithValue("maxResetCount", NpgsqlDbType.Integer, definition.MaxResetCount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task InsertInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long instanceId, LeaderboardDefinitionSpec definition, LeaderboardInstanceSpec initial, long currentTime, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"INSERT INTO {InstanceTable} (instance_id, leaderboard_id, state, activation_date, visible) VALUES (@instanceId, @leaderboardId, @state, @activationDate, @visible)", connection, transaction);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, definition.LeaderboardId);
            command.Parameters.AddWithValue("state", NpgsqlDbType.Smallint, (short)(definition.IsEnabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded));
            command.Parameters.AddWithValue("activationDate", NpgsqlDbType.Bigint, initial.ActivationDate == 0 ? currentTime : initial.ActivationDate);
            command.Parameters.AddWithValue("visible", NpgsqlDbType.Boolean, definition.IsEnabled);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task TerminalizeInstanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($@"UPDATE {InstanceTable} target SET state = 5,
                visible = EXISTS (SELECT 1 FROM {RewardTable} reward WHERE reward.instance_id = target.instance_id)
                WHERE target.leaderboard_id = @leaderboardId AND target.instance_id = @instanceId AND target.state < 5", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task RepairActivationDateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, LeaderboardInstanceSpec initial, long currentTime, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {InstanceTable} SET activation_date = @activationDate WHERE leaderboard_id = @leaderboardId AND state < 5 AND activation_date = 0", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("activationDate", NpgsqlDbType.Bigint, initial.ActivationDate == 0 ? currentTime : initial.ActivationDate);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task SetDefinitionDisabledAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"UPDATE {DefinitionTable} SET is_enabled = FALSE WHERE leaderboard_id = @leaderboardId", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task DeleteActiveMappingsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long leaderboardId, long instanceId, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"DELETE FROM {MetaEntryTable} WHERE leaderboard_id = @leaderboardId AND instance_id = @instanceId", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, leaderboardId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, instanceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task InsertMappingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, LeaderboardMetaMapping mapping, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"INSERT INTO {MetaEntryTable} (leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id) VALUES (@leaderboardId, @instanceId, @subLeaderboardId, @subInstanceId)", connection, transaction);
            command.Parameters.AddWithValue("leaderboardId", NpgsqlDbType.Bigint, mapping.LeaderboardId);
            command.Parameters.AddWithValue("instanceId", NpgsqlDbType.Bigint, mapping.InstanceId);
            command.Parameters.AddWithValue("subLeaderboardId", NpgsqlDbType.Bigint, mapping.SubLeaderboardId);
            command.Parameters.AddWithValue("subInstanceId", NpgsqlDbType.Bigint, mapping.SubInstanceId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static async Task<LeaderboardSnapshot> ReadSnapshotAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<long> desiredIds, int normalArchiveLimit, CancellationToken cancellationToken)
        {
            long[] ids = desiredIds.Distinct().OrderBy(id => unchecked((ulong)id)).ToArray();
            if (ids.Length == 0)
                return new();

            List<DBLeaderboard> definitions = await ReadSnapshotDefinitionsAsync(connection, transaction, ids, cancellationToken);
            List<DBLeaderboardInstance> nonterminal = await ReadSnapshotInstancesAsync(connection, transaction, ids,
                $"state < {(short)LeaderboardState.eLBS_Rewarded} ORDER BY (instance_id < 0), instance_id", cancellationToken);
            List<DBLeaderboardInstance> normalArchives = normalArchiveLimit == 0 ? new() : await ReadBoundedArchivesAsync(connection, transaction, ids, normalArchiveLimit, cancellationToken);
            HashSet<long> snapshotInstanceIds = nonterminal.Concat(normalArchives).Select(instance => instance.InstanceId).ToHashSet();

            List<DBMetaEntry> mappings = new();
            if (snapshotInstanceIds.Count > 0)
            {
                string sql = $"SELECT leaderboard_id, instance_id, sub_leaderboard_id, sub_instance_id FROM {MetaEntryTable} WHERE leaderboard_id = ANY(@leaderboardIds) AND instance_id = ANY(@instanceIds)";
                SnapshotMetaMappingCommandHook?.Invoke(sql);
                await using NpgsqlCommand command = new(sql, connection, transaction);
                command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
                command.Parameters.AddWithValue("instanceIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, snapshotInstanceIds.ToArray());
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    mappings.Add(new DBMetaEntry { LeaderboardId = reader.GetInt64(0), InstanceId = reader.GetInt64(1), SubLeaderboardId = reader.GetInt64(2), SubInstanceId = reader.GetInt64(3) });
            }
            return new LeaderboardSnapshot(definitions, nonterminal, normalArchives, mappings.OrderBy(mapping => unchecked((ulong)mapping.LeaderboardId)).ThenBy(mapping => unchecked((ulong)mapping.InstanceId)).ThenBy(mapping => unchecked((ulong)mapping.SubLeaderboardId)));
        }

        private static async Task<List<DBLeaderboard>> ReadSnapshotDefinitionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] ids, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT leaderboard_id, prototype_name, active_instance_id, is_enabled, start_time, max_reset_count FROM {DefinitionTable} WHERE leaderboard_id = ANY(@leaderboardIds) ORDER BY (leaderboard_id < 0), leaderboard_id", connection, transaction);
            command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            List<DBLeaderboard> definitions = new();
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                definitions.Add(new DBLeaderboard { LeaderboardId = reader.GetInt64(0), PrototypeName = reader.GetString(1), ActiveInstanceId = reader.IsDBNull(2) ? 0 : reader.GetInt64(2), IsEnabled = reader.GetBoolean(3), StartTime = reader.GetInt64(4), MaxResetCount = reader.GetInt32(5) });
            return definitions;
        }

        private static async Task<List<DBLeaderboardInstance>> ReadSnapshotInstancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] ids, string predicateAndOrder, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($"SELECT instance_id, leaderboard_id, state, activation_date, visible FROM {InstanceTable} WHERE leaderboard_id = ANY(@leaderboardIds) AND {predicateAndOrder}", connection, transaction);
            command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            return (await ReadInstancesAsync(command, cancellationToken)).ToList();
        }

        private static async Task<List<DBLeaderboardInstance>> ReadBoundedArchivesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long[] ids, int limit, CancellationToken cancellationToken)
        {
            await using NpgsqlCommand command = new($@"SELECT instance_id, leaderboard_id, state, activation_date, visible
                FROM (
                    SELECT instance_id, leaderboard_id, state, activation_date, visible,
                        ROW_NUMBER() OVER (PARTITION BY leaderboard_id ORDER BY (instance_id < 0) DESC, instance_id DESC) AS archive_rank
                    FROM {InstanceTable}
                    WHERE leaderboard_id = ANY(@leaderboardIds) AND state >= {(short)LeaderboardState.eLBS_Rewarded} AND visible
                ) archives
                WHERE archive_rank <= @limit
                ORDER BY (leaderboard_id < 0), leaderboard_id, (instance_id < 0) DESC, instance_id DESC", connection, transaction);
            command.Parameters.AddWithValue("leaderboardIds", NpgsqlDbType.Array | NpgsqlDbType.Bigint, ids);
            command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, limit);
            return (await ReadInstancesAsync(command, cancellationToken)).ToList();
        }

        private readonly record struct LeaderboardEntriesRead(bool Found, IReadOnlyList<DBLeaderboardEntry> Entries);

        private sealed class LeaderboardWriteAbortedException : Exception
        {
            internal LeaderboardWriteAbortedException(LeaderboardStoreResult result)
            {
                Result = result;
            }

            internal LeaderboardStoreResult Result { get; }
        }
    }
}
