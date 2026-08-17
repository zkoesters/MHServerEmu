using Gazillion;
using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.System.Time;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.Games;

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

        private bool _isEnabled;

        public GameServiceState State { get; private set; } = GameServiceState.Created;

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

            // TODO: Handle this in the leaderboard service thread and send the report to the game instance as a service message.
            const ushort MuxChannel = 1;

            Task.Run(() =>
            {
                client.SendMessage(MuxChannel, NetMessageLeaderboardReportClient.CreateBuilder()
                    .SetReport(_database.GetLeaderboardReport(request))
                    .Build());
            });

            return true;
        }
    }
}
