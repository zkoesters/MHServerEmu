using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess;

namespace MHServerEmu.Leaderboards
{
    /// <summary>
    /// Manages reward distribution for leaderboard participants.
    /// </summary>
    public class LeaderboardRewardManager
    {
        private static readonly Logger Logger = LogManager.CreateLogger();
        private readonly Dictionary<LeaderboardRewardKey, DBRewardEntry> _pendingRewards = new();
        private readonly Dictionary<LeaderboardRewardKey, PendingFinalization> _pendingFinalizations = new();

        private Queue<ServiceMessage.LeaderboardRewardRequest> _requestQueue = new();
        private Queue<ServiceMessage.LeaderboardRewardRequest> _processRequestQueue = new();
        private Queue<ServiceMessage.LeaderboardRewardConfirmation> _confirmationQueue = new();
        private Queue<ServiceMessage.LeaderboardRewardConfirmation> _processConfirmationQueue = new();

        private readonly object _queueLock = new();
        private readonly ILeaderboardStore _store;
        private readonly ILeaderboardPublisher _publisher;
        private readonly Func<TimeSpan> _clock;
        private readonly Action _fatal;
        private bool _stopped;

        public LeaderboardRewardManager(ILeaderboardStore store, ILeaderboardPublisher publisher)
            : this(store, publisher, () => Clock.UnixTime, () => { })
        {
        }

        public LeaderboardRewardManager(ILeaderboardStore store, ILeaderboardPublisher publisher, Func<TimeSpan> clock, Action fatal)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _fatal = fatal ?? throw new ArgumentNullException(nameof(fatal));
        }

        /// <summary>
        /// Enqueues a <see cref="ServiceMessage.LeaderboardRewardRequest"/> to be processed during the next update.
        /// </summary>
        public void OnLeaderboardRewardRequest(in ServiceMessage.LeaderboardRewardRequest request)
        {
            lock (_queueLock)
                _requestQueue.Enqueue(request);
        }

        /// <summary>
        /// Enqueues a <see cref="ServiceMessage.LeaderboardRewardConfirmation"/> to be processed during the next update.
        /// </summary>
        public void OnLeaderboardRewardConfirmation(in ServiceMessage.LeaderboardRewardConfirmation confirmation)
        {
            lock (_queueLock)
                _confirmationQueue.Enqueue(confirmation);
        }

        /// <summary>
        /// Processes queued messages.
        /// </summary>
        public void Update()
        {
            if (_stopped)
                return;

            // Swap queues
            lock (_queueLock)
            {
                (_requestQueue, _processRequestQueue) = (_processRequestQueue, _requestQueue);
                (_confirmationQueue, _processConfirmationQueue) = (_processConfirmationQueue, _confirmationQueue);
            }

            // Process confirmations first
            while (_processConfirmationQueue.Count > 0)
            {
                ServiceMessage.LeaderboardRewardConfirmation confirmation = _processConfirmationQueue.Dequeue();
                FinalizeReward((long)confirmation.LeaderboardId, (long)confirmation.InstanceId, confirmation.ParticipantId);
            }

            // Now initiate new requests
            while (_processRequestQueue.Count > 0)
            {
                ServiceMessage.LeaderboardRewardRequest request = _processRequestQueue.Dequeue();
                QueryRewards(request.ParticipantId);
            }

            TimeSpan now = _clock();
            List<LeaderboardRewardKey> due = _pendingFinalizations
                .Where(pair => pair.Value.DueTime <= now)
                .Select(pair => pair.Key)
                .ToList();
            foreach (LeaderboardRewardKey key in due)
            {
                FinalizeReward(key);
                if (_stopped)
                    break;
            }
        }

        public void Shutdown()
        {
            _stopped = true;
            _pendingFinalizations.Clear();
        }

        /// <summary>
        /// Queries the database for rewards for the specified participant and relays the data to the game instance service.
        /// </summary>
        private bool QueryRewards(ulong participantId)
        {
            // Query the database and exit early if there are no rewards to give
            List<DBRewardEntry> dbRewards;
            if (_store.GetPendingRewards((long)participantId, out IReadOnlyList<DBRewardEntry> rewards) == LeaderboardStoreResult.Success)
                dbRewards = rewards.ToList();
            else
                return false;
            if (dbRewards.Count == 0)
                return true;

            // Send reward information to game
            ServiceMessage.LeaderboardRewardEntry[]  rewardEntries = new ServiceMessage.LeaderboardRewardEntry[dbRewards.Count];
            for (int i = 0; i < dbRewards.Count; i++)
            {
                DBRewardEntry dbReward = dbRewards[i];
                Logger.Info($"Found reward for participant 0x{participantId:X}: leaderboardId={dbReward.LeaderboardId}, instanceId={dbReward.InstanceId}");
                rewardEntries[i] = new((ulong)dbReward.LeaderboardId, (ulong)dbReward.InstanceId, (ulong)dbReward.ParticipantId, (ulong)dbReward.RewardId, dbReward.Rank);
            }

            ServiceMessage.LeaderboardRewardRequestResponse requestResponse = new(participantId, rewardEntries);
            _publisher.Publish(requestResponse);
            foreach (DBRewardEntry reward in dbRewards)
                _pendingRewards[new(reward.LeaderboardId, reward.InstanceId, reward.ParticipantId)] = reward;

            return true;
        }

        /// <summary>
        /// Marks a leaderboard reward as distributed in the database.
        /// </summary>
        private bool FinalizeReward(long leaderboardId, long instanceId, ulong participantId)
        {
            LeaderboardRewardKey key = new(leaderboardId, instanceId, (long)participantId);
            if (FinalizeReward(key) == false)
                return false;

            _pendingRewards.Remove(key);

            return true;
        }

        private bool FinalizeReward(LeaderboardRewardKey key)
        {
            RewardFinalizationResult result = _store.FinalizeReward(key, (long)_clock().TotalSeconds);
            switch (result)
            {
                case RewardFinalizationResult.Finalized:
                case RewardFinalizationResult.AlreadyFinalized:
                    _pendingFinalizations.Remove(key);
                    _pendingRewards.Remove(key);
                    return true;

                case RewardFinalizationResult.NotFound:
                    _pendingFinalizations.Remove(key);
                    _pendingRewards.Remove(key);
                    Logger.Warn($"FinalizeReward(): Missing reward {key}");
                    return true;

                case RewardFinalizationResult.Failed:
                    ScheduleRetry(key);
                    return false;

                case RewardFinalizationResult.OutcomeUncertain:
                    _stopped = true;
                    _pendingFinalizations.Clear();
                    _fatal();
                    return false;

                default:
                    return false;
            }
        }

        private void ScheduleRetry(LeaderboardRewardKey key)
        {
            int retryCount = _pendingFinalizations.TryGetValue(key, out PendingFinalization pending) ? pending.RetryCount + 1 : 1;
            int delaySeconds = retryCount switch
            {
                1 => 1,
                2 => 2,
                3 => 4,
                4 => 8,
                5 => 16,
                _ => 30,
            };
            _pendingFinalizations[key] = new(retryCount, _clock() + TimeSpan.FromSeconds(delaySeconds));
        }

        private readonly record struct PendingFinalization(int RetryCount, TimeSpan DueTime);
    }
}
