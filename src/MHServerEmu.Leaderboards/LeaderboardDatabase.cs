using System.Diagnostics;
using System.Text.Json;
using Gazillion;
using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Leaderboards
{   
    /// <summary>
    /// A singleton that manages runtime leaderboard data.
    /// </summary>
    public class LeaderboardDatabase
    {
        private const ulong UpdateTimeIntervalMS = 30 * 1000;   // 30 seconds

        private static readonly Logger Logger = LogManager.CreateLogger();
        private static readonly string LeaderboardsDirectory = Path.Combine(FileHelper.DataDirectory, "Leaderboards");

        private readonly object _leaderboardLock = new();

        private readonly Dictionary<PrototypeGuid, Leaderboard> _leaderboards = new();
        private readonly Dictionary<PrototypeGuid, Leaderboard> _metaLeaderboards = new();
        private readonly Dictionary<ulong, string> _playerNames = new();
        private IPlayerStore _players;
        private readonly ILeaderboardStore _store;
        private readonly ILeaderboardPlayerNameResolver _nameResolver;
        private readonly ILeaderboardPrototypeCatalog _catalog;
        private readonly ILeaderboardPublisher _publisher;
        private readonly LeaderboardRuntimeOptions _options;
        private readonly Action _fatal;
        private LeaderboardArchiveCache<DBLeaderboardInstance> _archiveCache;

        internal LeaderboardRuntimeOptions Options { get => _options; }

        private readonly DoubleBufferQueue<ServiceMessage.LeaderboardScoreUpdateBatch> _scoreUpdateQueue = new();

        public bool IsInitialized { get; private set; }
        public SQLiteLeaderboardDBManager DBManager { get; private set; }
        public int LeaderboardCount { get => _leaderboards.Count; }
        public LeaderboardDatabase(ILeaderboardStore store, ILeaderboardPlayerNameResolver nameResolver,
            ILeaderboardPrototypeCatalog catalog, ILeaderboardPublisher publisher, LeaderboardRuntimeOptions options, Action fatal = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _fatal = fatal ?? (() => { });
            _archiveCache = new(_options.ArchiveCacheCapacity);
        }

        /// <summary>
        /// Initializes an explicitly owned runtime from the store's committed schedule snapshot.
        /// </summary>
        public bool Initialize()
        {
            if (_store == null)
                throw new InvalidOperationException("The compatibility database requires its legacy initializer.");

            // Schedule generation and validation must succeed before a persistence adapter is contacted.
            LeaderboardScheduleLoader loader = new(_catalog, Clock.UtcNowPrecise);
            if (loader.TryLoadOrCreate(_options.SchedulePath, _options.NormalArchiveLimit, out LeaderboardReconciliation reconciliation) == false)
                return false;

            if (_store.Initialize() != LeaderboardStoreResult.Success)
                return false;
            if (_store.ReconcileSchedule(reconciliation, out LeaderboardSnapshot snapshot) != LeaderboardStoreResult.Success)
                return false;

            ApplySnapshot(snapshot);

            PublishInitialState();
            IsInitialized = true;
            return true;
        }

        /// <summary>
        /// Initializes the <see cref="LeaderboardDatabase"/> instance.
        /// </summary>
        public bool Initialize(SQLiteLeaderboardDBManager instance, IPlayerStore players)
        {
            DBManager = instance;

            var stopwatch = Stopwatch.StartNew();

            var config = ConfigManager.Instance.GetConfig<LeaderboardsConfig>();

            // Create the leaderboards data directory if needed
            if (Directory.Exists(LeaderboardsDirectory) == false)
                Directory.CreateDirectory(LeaderboardsDirectory);

            // Initialize leaderboard database
            string databasePath = Path.Combine(LeaderboardsDirectory, config.DatabaseFile);
            bool noTables = false;
            DBManager.Initialize(databasePath, ref noTables);

            // Initialize leaderboards from prototypes if there is no data in the database
            string schedulePath = Path.Combine(LeaderboardsDirectory, config.ScheduleFile);
            if (noTables)
                GenerateTables(schedulePath);

            InitializePlayerNames(players);

            // Load the schedule and write changes to the database if needed
            List<DBLeaderboard> updatedLeaderboards = new();
            List<DBLeaderboardInstance> updatedInstances = new();
            if (LoadSchedule(schedulePath, updatedLeaderboards, updatedInstances))
                SaveScheduleChanges(updatedLeaderboards, updatedInstances);

            // Load leaderboards from the database
            LoadLeaderboards();

            // Send initial leaderboard state to game instances
            SendLeaderboardsToGames();

            IsInitialized = true;

            Logger.Info($"Initialized {_leaderboards.Count} leaderboards in {stopwatch.ElapsedMilliseconds} ms");
            return true;
        }

        internal void InitializePlayerNames(IPlayerStore players)
        {
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _playerNames.Clear();

            // Remove or disable this if the number of accounts gets out of hand.
            if (_players.GetPlayerNames(_playerNames))
                Logger.Info($"Loaded and cached {_playerNames.Count} player names");
        }

        /// <summary>
        /// Loads leaderboard schedule from JSON and adds updated models to provided lists.
        /// Returns <see langword="true"/> if any changes were applied.
        /// </summary>
        private bool LoadSchedule(string schedulePath, List<DBLeaderboard> updatedLeaderboards, List<DBLeaderboardInstance> updatedInstances)
        {
            // Load schedule
            LeaderboardScheduler[] schedulers;

            try
            {
                schedulers = FileHelper.DeserializeJson<LeaderboardScheduler[]>(schedulePath, LeaderboardScheduler.JsonSerializerOptions);
                LeaderboardScheduler.ValidateMetaLeaderboards(schedulers);
            }
            catch (Exception e)
            {
                return Logger.WarnReturn(false, $"LoadSchedule(): Schedule {schedulePath} deserialization failed - {e.Message}");
            }

            // Load current leaderboard data from the database
            DBLeaderboard[] oldDbLeaderboards = DBManager.GetLeaderboards();

            // Apply schedule changes
            foreach (LeaderboardScheduler scheduler in schedulers)
            {
                DBLeaderboard oldDbLeaderboard = oldDbLeaderboards.FirstOrDefault(lb => lb.LeaderboardId == (long)scheduler.LeaderboardId);
                if (oldDbLeaderboard == null)
                {
                    Logger.Warn($"LoadSchedule(): Loaded scheduler {scheduler}, but found no record for it in the database");
                    continue;
                }

                // Skip unchanged
                if (scheduler.IsEquivalent(oldDbLeaderboard))
                    continue;

                // Add changed leaderboard
                DBLeaderboard updatedLeaderboard = scheduler.ApplyToDBLeaderboard(oldDbLeaderboard);
                updatedLeaderboards.Add(updatedLeaderboard);

                // Apply changes to instances if needed
                if (oldDbLeaderboard.IsEnabled == false)
                {
                    if (scheduler.IsEnabled)
                    {
                        // IsEnabled: False -> True
                        // Add new instance
                        DateTime activationDate = scheduler.CalcNextUtcActivationDate();

                        updatedInstances.Add(new DBLeaderboardInstance
                        {
                            InstanceId = oldDbLeaderboard.ActiveInstanceId + 1,
                            LeaderboardId = (long)scheduler.LeaderboardId,
                            State = LeaderboardState.eLBS_Created,
                            ActivationDate = Clock.DateTimeToTimestamp(activationDate),
                            Visible = true
                        });
                    }
                    else
                    {
                        // IsEnabled: False -> False
                        // Deactivate active instances
                        List<DBLeaderboardInstance> instances = DBManager.GetInstances((long)scheduler.LeaderboardId, 0);
                        foreach (DBLeaderboardInstance instance in instances)
                        {
                            updatedInstances.Add(new DBLeaderboardInstance
                            {
                                InstanceId = instance.InstanceId,
                                LeaderboardId = instance.LeaderboardId,
                                State = LeaderboardState.eLBS_Rewarded,
                                ActivationDate = instance.ActivationDate,
                                Visible = false
                            });
                        }
                    }
                }
                else
                {
                    // Update instances
                    List<DBLeaderboardInstance> instances = DBManager.GetInstances((long)scheduler.LeaderboardId, 0);
                    foreach (DBLeaderboardInstance instance in instances)
                    {
                        if (scheduler.IsEnabled)
                        {
                            // IsEnabled: True -> True
                            // Update instance
                            long activationDate = instance.ActivationDate;

                            if (scheduler.StartTime != oldDbLeaderboard.GetStartDateTime() || scheduler.MaxResetCount != oldDbLeaderboard.MaxResetCount)
                            {
                                // Find next activation time
                                DateTime nextEvent = scheduler.CalcNextUtcActivationDate();
                                activationDate = Clock.DateTimeToTimestamp(nextEvent);
                            }

                            updatedInstances.Add(new DBLeaderboardInstance
                            {
                                InstanceId = instance.InstanceId,
                                LeaderboardId = instance.LeaderboardId,
                                State = instance.State,
                                ActivationDate = activationDate,
                                Visible = true
                            });
                        }
                        else
                        {
                            // IsEnabled: True -> False
                            // Deactivate active instance
                            updatedInstances.Add(new DBLeaderboardInstance
                            {
                                InstanceId = instance.InstanceId,
                                LeaderboardId = instance.LeaderboardId,
                                State = LeaderboardState.eLBS_Rewarded,
                                ActivationDate = instance.ActivationDate,
                                Visible = false
                            });
                        }
                    }
                }
            }

            Logger.Info($"Loaded leaderboard schedule from {Path.GetFileName(schedulePath)}");

            return updatedLeaderboards.Count > 0;
        }

        /// <summary>
        /// Reloads the leaderboard schedule from JSON and reapplies it if needed.
        /// </summary>
        public bool ReloadAndReapplySchedule()
        {
            if (_store == null)
                return false;

            LeaderboardScheduleLoader loader = new(_catalog, Clock.UtcNowPrecise);
            if (loader.TryLoadOrCreate(_options.SchedulePath, _options.NormalArchiveLimit, out LeaderboardReconciliation reconciliation) == false)
                return false;
            if (_store.ReconcileSchedule(reconciliation, out LeaderboardSnapshot snapshot) != LeaderboardStoreResult.Success)
                return false;

            ApplySnapshot(snapshot);
            PublishInitialState();
            return true;
        }

        private void ApplySnapshot(LeaderboardSnapshot snapshot)
        {
            lock (_leaderboardLock)
            {
                _leaderboards.Clear();
                _metaLeaderboards.Clear();
                IReadOnlyList<LeaderboardInstanceSpec> instances = snapshot.NonterminalInstances.Concat(snapshot.NormalArchiveInstances).ToArray();
                foreach (LeaderboardDefinitionSpec definition in snapshot.Definitions)
                {
                    if (_catalog.TryGetPrototype(definition.LeaderboardId, out LeaderboardPrototype prototype) == false)
                        continue;

                    DBLeaderboardInstance[] definitionInstances = instances.Where(instance => instance.LeaderboardId == definition.LeaderboardId)
                        .GroupBy(instance => instance.InstanceId)
                        .Select(group => group.First())
                        .Select(instance => new DBLeaderboardInstance
                        {
                            InstanceId = instance.InstanceId,
                            LeaderboardId = instance.LeaderboardId,
                            State = instance.State,
                            ActivationDate = instance.ActivationDate,
                            Visible = instance.Visible,
                        }).ToArray();
                    long activeInstanceId = definitionInstances.Where(instance => instance.State is LeaderboardState.eLBS_Created or LeaderboardState.eLBS_Active)
                        .OrderByDescending(instance => unchecked((ulong)instance.InstanceId)).Select(instance => instance.InstanceId).FirstOrDefault();
                    Leaderboard leaderboard = new(this, prototype, new DBLeaderboard
                    {
                        LeaderboardId = definition.LeaderboardId,
                        PrototypeName = definition.PrototypeName,
                        IsEnabled = definition.IsEnabled,
                        StartTime = definition.StartTime,
                        MaxResetCount = definition.MaxResetCount,
                        ActiveInstanceId = activeInstanceId,
                    }, definitionInstances);
                    if (prototype.IsMetaLeaderboard)
                        _metaLeaderboards.Add((PrototypeGuid)definition.LeaderboardId, leaderboard);
                    else
                        _leaderboards.Add((PrototypeGuid)definition.LeaderboardId, leaderboard);
                }
            }
        }

        /// <summary>
        /// Saves leaderboard schedule changes to the database.
        /// </summary>
        private void SaveScheduleChanges(List<DBLeaderboard> updatedLeaderboards, List<DBLeaderboardInstance> updatedInstances)
        {
            // Write changes to the database
            DBManager.UpdateLeaderboards(updatedLeaderboards);
            DBManager.UpdateOrInsertInstances(updatedInstances);

            Logger.Info($"Saved schedule changes affecting {updatedLeaderboards.Count} leaderboard(s) and {updatedInstances.Count} instance(s) to the database");
        }

        /// <summary>
        /// Applies leaderboard schedule changes to runtime data.
        /// </summary>
        private void ApplyScheduleChanges(List<DBLeaderboard> updatedLeaderboards, List<DBLeaderboardInstance> updatedInstances)
        {            
            foreach (DBLeaderboard updatedLeaderboard in updatedLeaderboards)
            {
                Leaderboard leaderboard = GetLeaderboard((PrototypeGuid)updatedLeaderboard.LeaderboardId);
                if (leaderboard == null)
                    continue;                

                // Update leaderboard scheduler
                leaderboard.Scheduler.Initialize(updatedLeaderboard);

                // Update instances
                foreach (DBLeaderboardInstance updatedInstance in updatedInstances.Where(instance => instance.LeaderboardId == updatedLeaderboard.LeaderboardId))
                    leaderboard.RefreshInstance(updatedInstance);
            }

            Logger.Info($"Applied schedule changes affecting {updatedLeaderboards.Count} leaderboard(s) and {updatedInstances.Count} instance(s) to runtime data");
        }

        /// <summary>
        /// Loads <see cref="Leaderboard"/> data from the database.
        /// </summary>
        private void LoadLeaderboards()
        {
            foreach (var dbLeaderboard in DBManager.GetLeaderboards())
            {
                PrototypeGuid leaderboardId = (PrototypeGuid)dbLeaderboard.LeaderboardId;
                PrototypeId dataRef = GameDatabase.GetDataRefByPrototypeGuid(leaderboardId);
                if (dataRef == PrototypeId.Invalid)
                {
                    Logger.Warn($"Failed GetDataRefByPrototypeGuid LeaderboardId = {leaderboardId}");
                    continue;
                }
                var proto = GameDatabase.GetPrototype<LeaderboardPrototype>(dataRef);
                if (proto == null)
                {
                    Logger.Warn($"Failed GetPrototype dataRef = {dataRef}");
                    continue;
                }

                var leaderboard = new Leaderboard(this, proto, dbLeaderboard,
                    DBManager.GetInstances(dbLeaderboard.LeaderboardId, proto.MaxArchivedInstances));
                if (proto.IsMetaLeaderboard)
                    _metaLeaderboards.Add(leaderboardId, leaderboard);
                else
                    _leaderboards.Add(leaderboardId, leaderboard);
            }
        }

        /// <summary>
        /// Sends the state of all leaderboards to the game instance service.
        /// </summary>
        private void SendLeaderboardsToGames()
        {
            List<ServiceMessage.LeaderboardStateChange> instances = new();

            foreach (var leaderboard in _leaderboards.Values)
                leaderboard.GetInstanceInfos(instances);

            foreach (var leaderboard in _metaLeaderboards.Values)
                leaderboard.GetInstanceInfos(instances);

            ServiceMessage.LeaderboardStateChangeList message = new(instances);
            ServerManager.Instance.SendMessageToService(GameServiceType.GameInstance, message);
        }

        /// <summary>
        /// Initializes database data.
        /// </summary>
        private void GenerateTables(string schedulePath)
        {
            List<DBLeaderboard> dbLeaderboards = new();
            List<DBLeaderboardInstance> dbInstances = new();
            List<LeaderboardScheduler> schedule = new();

            DateTime currentYear = new(DateTime.Now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long startTime = Clock.DateTimeToTimestamp(currentYear);

            foreach (PrototypeId dataRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<LeaderboardPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                LeaderboardPrototype proto = GameDatabase.GetPrototype<LeaderboardPrototype>(dataRef);
                if (proto == null || proto.DesignState != DesignWorkflowState.Live || proto.Public == false)
                    continue;

                PrototypeGuid leaderboardId = GameDatabase.GetPrototypeGuid(dataRef);
                ulong instanceId = Leaderboard.GenerateInitialInstanceId(leaderboardId);

                // Enabled all permanent leaderboards except for Anniversary2016 by default
                bool isEnabled = proto.ResetFrequency == LeaderboardResetFrequency.NeverReset;
                if (leaderboardId == (PrototypeGuid)16486420054343424221)   // Anniversary2016
                    isEnabled = false;

                DBLeaderboard dbLeaderboard = new()
                {
                    LeaderboardId = (long)leaderboardId,
                    PrototypeName = dataRef.GetNameFormatted(),
                    ActiveInstanceId = (long)instanceId,
                    IsEnabled = isEnabled,
                    StartTime = startTime,
                    MaxResetCount = 0
                };

                dbLeaderboards.Add(dbLeaderboard);
                schedule.Add(new(dbLeaderboard));

                dbInstances.Add(new DBLeaderboardInstance
                {
                    InstanceId = (long)instanceId,
                    LeaderboardId = (long)leaderboardId,
                    State = isEnabled ? LeaderboardState.eLBS_Created : LeaderboardState.eLBS_Rewarded,
                    ActivationDate = 0,
                    Visible = isEnabled
                });

                if (proto.IsMetaLeaderboard)
                {
                    List<DBMetaEntry> dbMetaEntries = new();
                    foreach (MetaLeaderboardEntryPrototype meta in proto.MetaLeaderboardEntries)
                    {
                        PrototypeGuid subLeaderboardId = GameDatabase.GetPrototypeGuid(meta.Leaderboard);
                        ulong subInstanceId = Leaderboard.GenerateInitialInstanceId(subLeaderboardId);
                        dbMetaEntries.Add(new DBMetaEntry
                        {
                            LeaderboardId = (long)leaderboardId,
                            InstanceId = (long)instanceId,
                            SubLeaderboardId = (long)subLeaderboardId,
                            SubInstanceId = (long)subInstanceId
                        });
                    }
                    DBManager.InsertMetaEntries(dbMetaEntries);
                }
            }

            string scheduleJson = JsonSerializer.Serialize(schedule, LeaderboardScheduler.JsonSerializerOptions);
            File.WriteAllText(schedulePath, scheduleJson);

            DBManager.InsertLeaderboards(dbLeaderboards);
            DBManager.UpdateOrInsertInstances(dbInstances);
        }

        /// <summary>
        /// Returns a <see cref="string"/> containing the name of the specified player participant. 
        /// </summary>
        public string GetPlayerNameById(ulong participantId)
        {
            lock (_leaderboardLock)
            {
                if (_nameResolver != null)
                    return _nameResolver.GetPlayerName(participantId);
                // Check name cache
                if (_playerNames.TryGetValue(participantId, out string playerName))
                    return playerName;

                // Query the database if not cached
                if (_players.TryGetPlayerName(participantId, out playerName) == false)
                {
                    playerName = $"Player{participantId}";
                    Logger.Warn($"GetPlayerNameById(): Failed to get player name for participant 0x{participantId:X}");
                }

                _playerNames[participantId] = playerName;
                return playerName;
            }
        }

        internal bool LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries)
        {
            if (_store != null)
                return _store.LoadEntries(instanceId, out entries) == LeaderboardStoreResult.Success;

            entries = DBManager.GetEntries(instanceId, false);
            return true;
        }

        internal bool TryLoadArchivedInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance)
        {
            LeaderboardArchiveCache<DBLeaderboardInstance> archiveCache = _archiveCache ??= new(_options?.ArchiveCacheCapacity ?? 1);
            if (archiveCache.TryGet((ulong)instanceId, out instance) && instance.LeaderboardId == leaderboardId)
                return true;

            int pageSize = Math.Min(_options?.ArchiveCacheCapacity ?? 1, 100);
            if (_store == null)
            {
                foreach (DBLeaderboardInstance loaded in DBManager.GetInstances(leaderboardId, pageSize).Reverse<DBLeaderboardInstance>())
                    archiveCache.Set((ulong)loaded.InstanceId, loaded);
                return archiveCache.TryGet((ulong)instanceId, out instance) && instance.LeaderboardId == leaderboardId;
            }

            long beforeInstanceId = 0;
            while (true)
            {
                if (_store.LoadVisibleInstances(leaderboardId, beforeInstanceId, pageSize, out IReadOnlyList<DBLeaderboardInstance> page) != LeaderboardStoreResult.Success)
                {
                    instance = null;
                    return false;
                }

                foreach (DBLeaderboardInstance loaded in page.Reverse())
                    archiveCache.Set((ulong)loaded.InstanceId, loaded);

                if (archiveCache.TryGet((ulong)instanceId, out instance) && instance.LeaderboardId == leaderboardId)
                    return true;
                if (page.Count < pageSize)
                    break;

                long nextBeforeInstanceId = page[^1].InstanceId;
                if (nextBeforeInstanceId == beforeInstanceId)
                    break;
                beforeInstanceId = nextBeforeInstanceId;
            }

            instance = null;
            return false;
        }

        internal bool ActivateInstance(long leaderboardId, long instanceId, LeaderboardState state)
        {
            if (_store != null)
                return state == LeaderboardState.eLBS_Active && PersistActivation(new LeaderboardActivation(leaderboardId, instanceId, instanceId)) == LeaderboardStoreResult.Success;
            return DBManager.UpdateActiveInstanceState(leaderboardId, instanceId, (int)state);
        }

        internal LeaderboardStoreResult PersistActivation(LeaderboardActivation request)
        {
            return _store?.ActivateInstance(request) ?? LeaderboardStoreResult.Failed;
        }

        internal LeaderboardStoreResult PersistExpiration(LeaderboardExpiration request)
        {
            return _store?.ExpireInstance(request) ?? LeaderboardStoreResult.Failed;
        }

        internal LeaderboardStoreResult PersistRotation(LeaderboardRotation request, out DBLeaderboardInstance committedInstance)
        {
            if (_store != null)
                return _store.RotateActiveInstance(request, out committedInstance);

            committedInstance = null;
            return LeaderboardStoreResult.Failed;
        }

        internal LeaderboardStoreResult PersistRewards(LeaderboardRewardGeneration request)
        {
            return _store?.GenerateRewards(request) ?? LeaderboardStoreResult.Failed;
        }

        internal void RequestControlledShutdown() => _fatal();

        internal LeaderboardStoreResult PersistVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot)
        {
            if (_store != null)
                return _store.MaintainVisibility(request, out snapshot);

            snapshot = new();
            return LeaderboardStoreResult.Failed;
        }

        internal LeaderboardStoreResult SaveEntries(long leaderboardId, long instanceId, IEnumerable<DBLeaderboardEntry> entries)
        {
            if (_store != null)
                return _store.SaveScoreBatch(new LeaderboardScoreBatch(leaderboardId, instanceId, LeaderboardState.eLBS_Active, entries));

            DBManager.UpdateOrInsertEntries(entries.ToList());
            return LeaderboardStoreResult.Success;
        }

        internal void UpdateInstanceState(long leaderboardId, long instanceId, LeaderboardState state)
        {
            if (_store == null)
                DBManager.UpdateInstanceState(instanceId, (int)state);
        }

        internal void InsertInstance(DBLeaderboardInstance instance)
        {
            if (_store == null)
                DBManager.InsertInstance(instance);
        }

        internal void InsertMetaEntries(IEnumerable<DBMetaEntry> entries)
        {
            if (_store == null)
                DBManager.InsertMetaEntries(entries.ToList());
        }

        internal IReadOnlyList<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId)
        {
            if (_store == null)
                return DBManager.GetMetaEntries(leaderboardId, instanceId);

            return _store.LoadMetaMappings(leaderboardId, instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) == LeaderboardStoreResult.Success
                ? mappings.Select(mapping => new DBMetaEntry
                {
                    LeaderboardId = mapping.LeaderboardId,
                    InstanceId = mapping.InstanceId,
                    SubLeaderboardId = mapping.SubLeaderboardId,
                    SubInstanceId = mapping.SubInstanceId,
                }).ToArray()
                : Array.Empty<DBMetaEntry>();
        }

        internal LeaderboardStoreResult GenerateRewards(long leaderboardId, long expectedActiveInstanceId, long instanceId,
            IEnumerable<DBRewardEntry> rewards)
        {
            if (_store != null)
                return PersistRewards(new LeaderboardRewardGeneration(leaderboardId, expectedActiveInstanceId, instanceId,
                    LeaderboardState.eLBS_Expired, rewards));

            DBManager.InsertRewards(rewards.ToList());
            return LeaderboardStoreResult.Success;
        }

        internal void Publish(ServiceMessage.LeaderboardStateChange change)
        {
            if (_publisher != null)
                _publisher.Publish(change);
            else
                ServerManager.Instance.SendMessageToService(GameServiceType.GameInstance, change);
        }

        private void PublishInitialState()
        {
            List<ServiceMessage.LeaderboardStateChange> changes = new();
            foreach (Leaderboard leaderboard in GetLeaderboards())
                leaderboard.GetInstanceInfos(changes);
            _publisher.Publish(changes);
        }

        /// <summary>
        /// Builds a <see cref="LeaderboardReport"/> for the provided <see cref="NetMessageLeaderboardRequest"/>.
        /// </summary>
        public LeaderboardReport GetLeaderboardReport(NetMessageLeaderboardRequest request)
        {
            PrototypeGuid leaderboardId = 0;
            ulong instanceId = 0;

            LeaderboardReport.Builder report = LeaderboardReport.CreateBuilder()
                .SetNextUpdateTimeIntervalMS(UpdateTimeIntervalMS);

            lock (_leaderboardLock)
            {
                if (request.HasPlayerScoreQuery)
                {
                    LeaderboardPlayerScoreQuery query = request.PlayerScoreQuery;
                    leaderboardId = (PrototypeGuid)query.LeaderboardId;
                    instanceId = query.InstanceId;
                    ulong playerId = query.PlayerId;
                    ulong avatarId = query.HasAvatarId ? query.AvatarId : 0;

                    if (GetLeaderboardScoreData(leaderboardId, instanceId, playerId, avatarId, out LeaderboardScoreData scoreData))
                        report.SetScoreData(scoreData);
                }

                if (request.HasGuildScoreQuery) // Not used
                {
                    LeaderboardGuildScoreQuery query = request.GuildScoreQuery;
                    leaderboardId = (PrototypeGuid)query.LeaderboardId;
                    instanceId = query.InstanceId;
                    ulong guildId = query.GuildId;

                    if (GetLeaderboardScoreData(leaderboardId, instanceId, guildId, 0, out LeaderboardScoreData scoreData))
                        report.SetScoreData(scoreData);
                }

                if (request.HasMetaScoreQuery) // Tournament: Civil War
                {
                    LeaderboardMetaScoreQuery query = request.MetaScoreQuery;
                    leaderboardId = (PrototypeGuid)query.LeaderboardId;
                    instanceId = query.InstanceId;
                    ulong playerId = query.PlayerId;

                    if (GetLeaderboardScoreData(leaderboardId, instanceId, playerId, 0, out LeaderboardScoreData scoreData))
                        report.SetScoreData(scoreData);
                }

                if (request.HasDataQuery)
                {
                    LeaderboardDataQuery query = request.DataQuery;
                    leaderboardId = (PrototypeGuid)query.LeaderboardId;
                    instanceId = query.InstanceId;

                    if (GetLeaderboardTableData(leaderboardId, instanceId, out LeaderboardTableData tableData))
                        report.SetTableData(tableData);
                }
            }

            report.SetLeaderboardId((ulong)leaderboardId).SetInstanceId(instanceId);

            return report.Build();
        }

        /// <summary>
        /// Retrieves the <see cref="LeaderboardTableData"/> for the specified leaderboard instance.
        /// </summary>
        private bool GetLeaderboardTableData(PrototypeGuid leaderboardId, ulong instanceId, out LeaderboardTableData tableData)
        {
            tableData = null;
            
            Leaderboard leaderboard = GetLeaderboard(leaderboardId);
            if (leaderboard == null)
                return false;

            LeaderboardInstance instance = leaderboard.GetInstance(instanceId, true);
            if (instance == null)
                return false;

            tableData = instance.GetTableData();
            return true;
        }

        /// <summary>
        /// Builds <see cref="LeaderboardScoreData"/> for a participant in the specified leaderboard instance.
        /// </summary>
        private bool GetLeaderboardScoreData(PrototypeGuid leaderboardId, ulong instanceId, ulong participantId, ulong avatarId, 
            out LeaderboardScoreData scoreData)
        {
            scoreData = null;

            Leaderboard leaderboard = GetLeaderboard(leaderboardId);
            if (leaderboard == null)
                return false;

            LeaderboardType type = leaderboard.Prototype.Type;

            LeaderboardInstance instance = leaderboard.GetInstance(instanceId, true);
            if (instance == null)
                return false;

            LeaderboardEntry entry;
            if (type == LeaderboardType.MetaLeaderboard)
            {
                PrototypeGuid subLeaderboardId = instance.GetSubLeaderboardId(participantId);
                entry = instance.GetEntry((ulong)subLeaderboardId, avatarId);
            }
            else
            {
                entry = instance.GetEntry(participantId, avatarId);
            }

            if (entry == null)
                return false;

            LeaderboardScoreData.Builder scoreDataBuilder = LeaderboardScoreData.CreateBuilder()
                .SetLeaderboardId((ulong)leaderboardId);

            if (instanceId != 0)
                scoreDataBuilder.SetInstanceId(instanceId);

            if (type == LeaderboardType.Player) 
            {
                scoreDataBuilder.SetAvatarId(avatarId);
                scoreDataBuilder.SetPlayerId(participantId);
            }

            if (type == LeaderboardType.Guild)
                scoreDataBuilder.SetGuildId(participantId);

            scoreDataBuilder.SetScore(entry.Score);
            scoreDataBuilder.SetPercentileBucket((uint)instance.GetPercentileBucket(entry));

            scoreData = scoreDataBuilder.Build();

            return true;
        }

        /// <summary>
        /// Returns the <see cref="Leaderboard"/> with the specified <see cref="PrototypeGuid"/>.
        /// </summary>
        public Leaderboard GetLeaderboard(PrototypeGuid leaderboardId)
        {
            lock (_leaderboardLock)
            {
                if (_leaderboards.TryGetValue(leaderboardId, out Leaderboard leaderboard))
                    return leaderboard;

                if (_metaLeaderboards.TryGetValue(leaderboardId, out Leaderboard metaLeaderboard))
                    return metaLeaderboard;

                return null;
            }
        }

        /// <summary>
        /// Enqueues a <see cref="ServiceMessage.LeaderboardScoreUpdateBatch"/> to be processed during the next update.
        /// </summary>
        public void EnqueueLeaderboardScoreUpdate(in ServiceMessage.LeaderboardScoreUpdateBatch leaderboardScoreUpdateBatch)
        {
            _scoreUpdateQueue.Enqueue(leaderboardScoreUpdateBatch);
        }

        /// <summary>
        /// Processes queued <see cref="ServiceMessage.LeaderboardScoreUpdateBatch"/> instances.
        /// </summary>
        public void ProcessLeaderboardScoreUpdateQueue()
        {
            _scoreUpdateQueue.Swap();

            while (_scoreUpdateQueue.CurrentCount > 0)
            {
                ServiceMessage.LeaderboardScoreUpdateBatch batch = _scoreUpdateQueue.Dequeue();
                for (int i = 0; i < batch.Count; i++)
                {
                    ref ServiceMessage.LeaderboardScoreUpdate update = ref batch[i];
                    Leaderboard leaderboard = GetLeaderboard((PrototypeGuid)update.LeaderboardId);
                    leaderboard?.OnScoreUpdate(ref update);
                }

                batch.Destroy();
            }
        }

        /// <summary>
        /// Adds all <see cref="Leaderboard">Leaderboards</see> to the provided <see cref="List{T}"/>.
        /// </summary>
        public void GetLeaderboards(List<Leaderboard> leaderboards)
        {
            lock (_leaderboardLock)
            {
                leaderboards.AddRange(_leaderboards.Values);
                leaderboards.AddRange(_metaLeaderboards.Values);
            }
        }

        public IReadOnlyList<Leaderboard> GetLeaderboards()
        {
            lock (_leaderboardLock)
                return _leaderboards.Values.Concat(_metaLeaderboards.Values).ToList();
        }

        /// <summary>
        /// Updates the state of all <see cref="Leaderboard">Leaderboards</see>.
        /// </summary>
        public void UpdateState()
        {
            ProcessLeaderboardScoreUpdateQueue();

            using var leaderboardsHandle = ListPool<Leaderboard>.Instance.Get(out List<Leaderboard> leaderboards);
            GetLeaderboards(leaderboards);

            DateTime updateTime = Clock.UtcNowPrecise;
            foreach (Leaderboard leaderboard in leaderboards)
                leaderboard.UpdateState(updateTime);
        }

        /// <summary>
        /// Saves all <see cref="LeaderboardEntry"/> instances for all active leaderboards to the database.
        /// </summary>
        public bool Save()
        {
            bool saved = true;
            using var leaderboardsHandle = ListPool<Leaderboard>.Instance.Get(out List<Leaderboard> leaderboards);
            GetLeaderboards(leaderboards);

            foreach (var leaderboard in leaderboards)                
                saved &= leaderboard.ActiveInstance?.SaveEntries(true) ?? true;

            return saved;
        }

        /// <summary>
        /// Searches for the <see cref="LeaderboardInstance"/> with the specified instance id.
        /// </summary>
        public LeaderboardInstance FindInstance(ulong instanceId)
        {
            using var leaderboardsHandle = ListPool<Leaderboard>.Instance.Get(out List<Leaderboard> leaderboards);
            GetLeaderboards(leaderboards);

            foreach (Leaderboard leaderboard in leaderboards)
                foreach (LeaderboardInstance instance in leaderboard.Instances)
                    if (instance.InstanceId == instanceId)
                        return instance;

            return null;
        }
    }
}
