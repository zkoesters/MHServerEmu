using Gazillion;
using MHServerEmu.Core.Collections;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Memory;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Leaderboards
{   
    public class LeaderboardDatabase
    {
        private const ulong UpdateTimeIntervalMS = 30 * 1000;   // 30 seconds

        private static readonly Logger Logger = LogManager.CreateLogger();
        private readonly object _leaderboardLock = new();
        private readonly object _scoreUpdateLock = new();

        private readonly Dictionary<PrototypeGuid, Leaderboard> _leaderboards = new();
        private readonly Dictionary<PrototypeGuid, Leaderboard> _metaLeaderboards = new();
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
        /// Reloads the leaderboard schedule from JSON and reapplies it if needed.
        /// </summary>
        public bool ReloadAndReapplySchedule()
        {
            lock (_scoreUpdateLock)
            {
                ProcessLeaderboardScoreUpdateQueue();
                if (Save() == false)
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
        /// Returns a <see cref="string"/> containing the name of the specified player participant.
        /// </summary>
        public string GetPlayerNameById(ulong participantId)
        {
            lock (_leaderboardLock)
                return _nameResolver.GetPlayerName(participantId);
        }

        internal bool LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries)
        {
            return _store.LoadEntries(instanceId, out entries) == LeaderboardStoreResult.Success;
        }

        internal bool TryLoadArchivedInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance)
        {
            LeaderboardArchiveCache<DBLeaderboardInstance> archiveCache = _archiveCache ??= new(_options?.ArchiveCacheCapacity ?? 1);
            if (archiveCache.TryGet((ulong)instanceId, out instance) && instance.LeaderboardId == leaderboardId)
                return true;

            int pageSize = Math.Min(_options?.ArchiveCacheCapacity ?? 1, 100);
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
            return state == LeaderboardState.eLBS_Active && PersistActivation(new LeaderboardActivation(leaderboardId, instanceId, instanceId)) == LeaderboardStoreResult.Success;
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
            return _store.RotateActiveInstance(request, out committedInstance);
        }

        internal LeaderboardStoreResult PersistRewards(LeaderboardRewardGeneration request)
        {
            return _store?.GenerateRewards(request) ?? LeaderboardStoreResult.Failed;
        }

        internal void RequestControlledShutdown() => _fatal();

        internal LeaderboardStoreResult PersistVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot)
        {
            return _store.MaintainVisibility(request, out snapshot);
        }

        internal LeaderboardStoreResult SaveEntries(long leaderboardId, long instanceId, IEnumerable<DBLeaderboardEntry> entries)
        {
            return _store.SaveScoreBatch(new LeaderboardScoreBatch(leaderboardId, instanceId, LeaderboardState.eLBS_Active, entries));
        }

        internal IReadOnlyList<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId)
        {
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
            return PersistRewards(new LeaderboardRewardGeneration(leaderboardId, expectedActiveInstanceId, instanceId,
                LeaderboardState.eLBS_Expired, rewards));
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
            lock (_scoreUpdateLock)
                _scoreUpdateQueue.Enqueue(leaderboardScoreUpdateBatch);
        }

        /// <summary>
        /// Processes queued <see cref="ServiceMessage.LeaderboardScoreUpdateBatch"/> instances.
        /// </summary>
        public void ProcessLeaderboardScoreUpdateQueue()
        {
            lock (_scoreUpdateLock)
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
