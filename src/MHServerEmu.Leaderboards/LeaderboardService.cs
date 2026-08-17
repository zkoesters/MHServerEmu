using Gazillion;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.Games;
using MHServerEmu.Games.GameData;
using MHServerEmu.Leaderboards.Administration;

namespace MHServerEmu.Leaderboards
{
    /// <summary>
    /// Handles leaderboard messages.
    /// </summary>
    public class LeaderboardService : IGameService
    {
        private const int UpdateTimeMS = 1000;

        private static readonly Logger Logger = LogManager.CreateLogger();

        private readonly LeaderboardDatabase _database;
        private readonly LeaderboardRewardManager _rewardManager;
        private readonly LeaderboardServiceMailbox _mailbox;

        private bool _isEnabled;

        public GameServiceState State { get; private set; } = GameServiceState.Created;
        public ILeaderboardAdministration Administration { get => _mailbox; }
        internal bool CanAdminister { get => State == GameServiceState.Running && _isEnabled && _database.IsInitialized; }

        public LeaderboardService(IPlayerStore players, ILeaderboardStore leaderboards)
        {
            if (players == null) throw new ArgumentNullException(nameof(players));
            if (leaderboards == null) throw new ArgumentNullException(nameof(leaderboards));

            LeaderboardsConfig config = ConfigManager.Instance.GetConfig<LeaderboardsConfig>();
            string schedulePath = Path.Combine(FileHelper.DataDirectory, "Leaderboards", config.ScheduleFile);
            ILeaderboardPublisher publisher = new ServerManagerLeaderboardPublisher();
            _database = new LeaderboardDatabase(leaderboards, new PlayerStoreLeaderboardNameResolver(players),
                new GameDatabaseLeaderboardPrototypeCatalog(), publisher,
                new LeaderboardRuntimeOptions(schedulePath, config.NormalArchiveLimit, config.AutoSaveIntervalMinutes), Shutdown);
            _rewardManager = new LeaderboardRewardManager(leaderboards, publisher, () => Clock.UnixTime, Shutdown);
            _mailbox = new(this);
        }

        #region IGameService Implementation

        public void Run()
        {
            State = GameServiceState.Starting;

            var config = ConfigManager.Instance.GetConfig<GameOptionsConfig>();
            _isEnabled = config.LeaderboardsEnabled;

            if (_isEnabled == false)
            {
                State = GameServiceState.Running;
                return;
            }

            if (_database.Initialize() == false)
            {
                State = GameServiceState.Shutdown;
                return;
            }

            State = GameServiceState.Running;

            while (State == GameServiceState.Running)
            {
                _mailbox.ProcessMessages();

                // Update state for instances
                _database.UpdateState();

                // Process rewards
                _rewardManager.Update();

                Thread.Sleep(UpdateTimeMS);
            }

            State = GameServiceState.Shutdown;
        }

        public void Shutdown() 
        {
            if (_isEnabled)
            {
                _database?.Save();
                _rewardManager?.Shutdown();
                State = GameServiceState.ShuttingDown;
            }
            else
            {
                State = GameServiceState.Shutdown;
            }
        }

        public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage
        {
            switch (message)
            {
                case ServiceMessage.RouteMessage routeMailboxMessage:
                    OnRouteMailboxMessage(routeMailboxMessage);
                    break;

                case ServiceMessage.LeaderboardScoreUpdateBatch leaderboardScoreUpdateBatch:
                    _database.EnqueueLeaderboardScoreUpdate(leaderboardScoreUpdateBatch);
                    break;

                case ServiceMessage.LeaderboardRewardRequest leaderboardRewardRequest:
                    _rewardManager.OnLeaderboardRewardRequest(leaderboardRewardRequest);
                    break;

                case ServiceMessage.LeaderboardRewardConfirmation leaderboardRewardConfirmation:
                    _rewardManager.OnLeaderboardRewardConfirmation(leaderboardRewardConfirmation);
                    break;

                default:
                    Logger.Warn($"ReceiveServiceMessage(): Unhandled service message type {typeof(T).Name}");
                    break;
            }
        }

        public void GetStatus(Dictionary<string, long> statusDict)
        {
            statusDict["Leaderboards"] = _database != null ? _database.LeaderboardCount : 0;
        }

        private void OnRouteMailboxMessage(in ServiceMessage.RouteMessage routeMailboxMessage)
        {
            if (routeMailboxMessage.Protocol != typeof(ClientToGameServerMessage))
            {
                Logger.Warn($"OnRouteMailboxMessage(): Unhandled protocol {routeMailboxMessage.Protocol.Name}");
                return;
            }

            IFrontendClient client = routeMailboxMessage.Client;
            MailboxMessage message = routeMailboxMessage.Message;

            switch ((ClientToGameServerMessage)message.Id)
            {
                case ClientToGameServerMessage.NetMessageLeaderboardRequest:            OnLeaderboardRequest(client, message); break;

                default: Logger.Warn($"OnRouteMailboxMessage(): Unhandled {(ClientToGameServerMessage)message.Id} [{message.Id}]"); break;
            }
        }

        #endregion

        private bool OnLeaderboardRequest(IFrontendClient client, MailboxMessage message)
        {
            var request = message.As<NetMessageLeaderboardRequest>();
            if (request == null) return Logger.WarnReturn(false, $"OnLeaderboardRequest(): Failed to retrieve message");

            //Logger.Trace($"Received NetMessageLeaderboardRequest for {GameDatabase.GetPrototypeNameByGuid((PrototypeGuid)request.DataQuery.LeaderboardId)}");

            _mailbox.PostLeaderboardRequest(client, request);

            return true;
        }

        internal LeaderboardAdminResult ReloadSchedule()
        {
            if (CanAdminister == false)
                return LeaderboardAdminResult.Unavailable;

            return _database.ReloadAndReapplySchedule() ? LeaderboardAdminResult.Success : LeaderboardAdminResult.Failed;
        }

        internal void SendLeaderboardReport(IFrontendClient client, NetMessageLeaderboardRequest request)
        {
            const ushort MuxChannel = 1;
            client.SendMessage(MuxChannel, NetMessageLeaderboardReportClient.CreateBuilder()
                .SetReport(_database.GetLeaderboardReport(request))
                .Build());
        }

        internal LeaderboardInstanceResponse GetInstance(long instanceId)
        {
            if (CanAdminister == false)
                return new(LeaderboardAdminResult.Unavailable, null);

            LeaderboardInstance instance = _database.FindInstance((ulong)instanceId);
            return instance == null
                ? new(LeaderboardAdminResult.NotFound, null)
                : new(LeaderboardAdminResult.Success, ToSummary(instance));
        }

        internal LeaderboardResponse GetLeaderboard(long leaderboardId)
        {
            if (CanAdminister == false)
                return new(LeaderboardAdminResult.Unavailable, null);

            Leaderboard leaderboard = _database.GetLeaderboard((PrototypeGuid)leaderboardId);
            return leaderboard == null
                ? new(LeaderboardAdminResult.NotFound, null)
                : new(LeaderboardAdminResult.Success, ToSummary(leaderboard));
        }

        internal LeaderboardsResponse GetLeaderboards()
        {
            if (CanAdminister == false)
                return new(LeaderboardAdminResult.Unavailable, Array.Empty<LeaderboardSummary>());

            return new(LeaderboardAdminResult.Success, Array.AsReadOnly(_database.GetLeaderboards().Select(ToSummary).ToArray()));
        }

        private static LeaderboardSummary ToSummary(Leaderboard leaderboard)
        {
            return new((long)leaderboard.LeaderboardId, leaderboard.Prototype.DataRef.GetNameFormatted(), leaderboard.Scheduler.IsEnabled,
                leaderboard.Scheduler.StartTime, leaderboard.ActiveInstance == null ? null : ToSummary(leaderboard.ActiveInstance), leaderboard.ToString());
        }

        private static LeaderboardInstanceSummary ToSummary(LeaderboardInstance instance)
        {
            return new((long)instance.InstanceId, (long)instance.LeaderboardId, instance.LeaderboardPrototype.DataRef.GetNameFormatted(), instance.State,
                instance.ActivationTime, instance.ExpirationTime, instance.ToString());
        }
    }
}
