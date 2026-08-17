using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;
using MHServerEmu.Leaderboards.Administration;
using System.Diagnostics;
using System.Reflection;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardAdministrationTests
    {
        [Fact]
        public void GetLeaderboards_ServiceUnavailable_ReturnsUnavailable()
        {
            ILeaderboardAdministration administration = new LeaderboardService(new PlayerStore(), new LeaderboardStore()).Administration;

            Assert.Equal(LeaderboardAdminResult.Unavailable, administration.GetLeaderboards(out _));
        }

        [Fact]
        public void LeaderboardRequest_DisabledService_SendsEmptyReportWithoutMailboxProcessing()
        {
            LeaderboardService service = new(new PlayerStore(), new LeaderboardStore());
            SetAutoProperty(service, "State", GameServiceState.Running);
            RecordingFrontendClient client = new();
            MailboxMessage message = new((uint)ClientToGameServerMessage.NetMessageLeaderboardRequest, NetMessageLeaderboardRequest.DefaultInstance);

            service.ReceiveServiceMessage(new ServiceMessage.RouteMessage(client, typeof(ClientToGameServerMessage), message));

            NetMessageLeaderboardReportClient report = Assert.IsType<NetMessageLeaderboardReportClient>(client.Message);
            Assert.True(report.HasReport);
            Assert.Equal(0UL, report.Report.LeaderboardId);
            Assert.Equal(0UL, report.Report.InstanceId);
            Assert.False(report.Report.HasScoreData);
            Assert.False(report.Report.HasTableData);
        }

        [Fact]
        public async Task GetLeaderboards_WaitsForTheServiceMailbox()
        {
            LeaderboardService service = CreateRunningService();
            ILeaderboardAdministration administration = service.Administration;

            Task<LeaderboardAdminResult> request = Task.Run(() => administration.GetLeaderboards(out _));
            for (int i = 0; i < 100 && request.IsCompleted == false; i++)
            {
                ServiceMailbox(service).ProcessMessages();
                Thread.Sleep(10);
            }

            Assert.Equal(LeaderboardAdminResult.Success, await request.WaitAsync(TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void GetLeaderboards_UnprocessedMailbox_ReturnsUnavailableAfterFiveSeconds()
        {
            ILeaderboardAdministration administration = CreateRunningService().Administration;
            Stopwatch stopwatch = Stopwatch.StartNew();

            LeaderboardAdminResult result = administration.GetLeaderboards(out _);

            Assert.Equal(LeaderboardAdminResult.Unavailable, result);
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(6));
        }

        [Fact]
        public void ReloadSchedule_DrainsScoresAndSavesBeforeReconciliation()
        {
            string source = File.ReadAllText(Path.Combine(FindRoot(), "src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs"));
            int drain = source.IndexOf("ProcessLeaderboardScoreUpdateQueue();", StringComparison.Ordinal);
            int save = source.IndexOf("Save()", drain, StringComparison.Ordinal);
            int reload = source.IndexOf("_store.ReconcileSchedule", save, StringComparison.Ordinal);

            Assert.InRange(drain, 0, save - 1);
            Assert.InRange(save, 0, reload - 1);
        }

        [Fact]
        public void ReloadSchedule_BlocksScoreAcceptanceUntilTheReplacementSnapshotIsApplied()
        {
            string source = File.ReadAllText(Path.Combine(FindRoot(), "src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs"));
            int reloadLock = source.IndexOf("lock (_scoreUpdateLock)", StringComparison.Ordinal);
            Assert.True(reloadLock >= 0);
            if (reloadLock < 0)
                return;
            int drain = source.IndexOf("ProcessLeaderboardScoreUpdateQueue();", reloadLock, StringComparison.Ordinal);
            int save = source.IndexOf("Save()", drain, StringComparison.Ordinal);
            int applySnapshot = source.IndexOf("ApplySnapshot(snapshot);", save, StringComparison.Ordinal);

            Assert.InRange(reloadLock, 0, drain - 1);
            Assert.InRange(drain, 0, save - 1);
            Assert.InRange(save, 0, applySnapshot - 1);
        }

        private static LeaderboardService CreateRunningService()
        {
            LeaderboardService service = new(new PlayerStore(), new LeaderboardStore());
            SetAutoProperty(service, "State", MHServerEmu.Core.Network.GameServiceState.Running);
            typeof(LeaderboardService).GetField("_isEnabled", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(service, true);
            object database = typeof(LeaderboardService).GetField("_database", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
            SetAutoProperty(database, "IsInitialized", true);
            return service;
        }

        private static MHServerEmu.Core.Network.ServiceMailbox ServiceMailbox(LeaderboardService service)
        {
            return (MHServerEmu.Core.Network.ServiceMailbox)typeof(LeaderboardService)
                .GetField("_mailbox", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);
        }

        private static void SetAutoProperty(object instance, string propertyName, object value)
        {
            instance.GetType().GetField($"<{propertyName}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(instance, value);
        }

        private static string FindRoot()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MHServerEmu.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Repository root not found.");
        }

        private sealed class PlayerStore : IPlayerStore
        {
            public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
            {
                playerDbId = 0;
                playerNameOut = null;
                return false;
            }

            public bool TryGetPlayerName(ulong playerDbId, out string playerName)
            {
                playerName = null;
                return false;
            }

            public bool GetPlayerNames(Dictionary<ulong, string> playerNames) => true;
            public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
            {
                lastLogoutTime = 0;
                return false;
            }

            public PlayerStoreResult LoadPlayerData(MHServerEmu.DatabaseAccess.Models.DBAccount account) => PlayerStoreResult.Failed;
            public PlayerStoreResult SavePlayerData(MHServerEmu.DatabaseAccess.Models.DBAccount account) => PlayerStoreResult.Failed;
        }

        private sealed class RecordingFrontendClient : IFrontendClient
        {
            public IMessage Message { get; private set; }
            public bool IsConnected => true;
            public IFrontendSession Session => null;
            public ulong DbId => 0;

            public void Disconnect() { }
            public void SuspendReceiveTimeout() { }
            public bool AssignSession(IFrontendSession session) => true;
            public bool HandleIncomingMessageBuffer(ushort muxId, in MessageBuffer messageBuffer) => true;
            public void SendMuxCommand(ushort muxId, MuxCommand command) { }
            public void SendMessage(ushort muxId, IMessage message) => Message = message;
            public void SendMessageList(ushort muxId, List<IMessage> messageList) { }
        }

        private sealed class LeaderboardStore : ILeaderboardStore
        {
            public LeaderboardStoreResult Initialize() => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ReconcileSchedule(LeaderboardReconciliation request, out LeaderboardSnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadEntries(long instanceId, out IReadOnlyList<DBLeaderboardEntry> entries) { entries = Array.Empty<DBLeaderboardEntry>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadInstance(long leaderboardId, long instanceId, out DBLeaderboardInstance instance) { instance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadMetaMappings(long leaderboardId, long instanceId, out IReadOnlyList<LeaderboardMetaMapping> mappings) { mappings = Array.Empty<LeaderboardMetaMapping>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult LoadVisibleInstances(long leaderboardId, long beforeInstanceId, int limit, out IReadOnlyList<DBLeaderboardInstance> instances) { instances = Array.Empty<DBLeaderboardInstance>(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult ActivateInstance(LeaderboardActivation request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult SaveScoreBatch(LeaderboardScoreBatch request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult ExpireInstance(LeaderboardExpiration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult RotateActiveInstance(LeaderboardRotation request, out DBLeaderboardInstance committedInstance) { committedInstance = null; return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult MaintainVisibility(LeaderboardVisibilityRequest request, out LeaderboardVisibilitySnapshot snapshot) { snapshot = new(); return LeaderboardStoreResult.Failed; }
            public LeaderboardStoreResult GenerateRewards(LeaderboardRewardGeneration request) => LeaderboardStoreResult.Failed;
            public LeaderboardStoreResult GetPendingRewards(long participantId, out IReadOnlyList<DBRewardEntry> rewards) { rewards = Array.Empty<DBRewardEntry>(); return LeaderboardStoreResult.Failed; }
            public RewardFinalizationResult FinalizeReward(LeaderboardRewardKey key, long rewardedDate) => RewardFinalizationResult.Failed;
        }
    }
}
