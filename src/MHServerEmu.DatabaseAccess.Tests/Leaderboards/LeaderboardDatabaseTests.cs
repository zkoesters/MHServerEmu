using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.DatabaseAccess.Tests.Leaderboards
{
    public class LeaderboardDatabaseTests
    {
        [Fact]
        public void PersistInitialData_WritesScheduleBeforeOneAtomicProviderCall()
        {
            FakeLeaderboardDBManager manager = new();
            LeaderboardDatabase database = new(manager);
            string schedulePath = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.json");
            List<DBLeaderboard> leaderboards = [new() { LeaderboardId = 1, PrototypeName = "Test", ActiveInstanceId = 2, IsEnabled = true }];
            List<DBLeaderboardInstance> instances = [new() { InstanceId = 2, LeaderboardId = 1 }];
            List<DBMetaEntry> metaEntries = [new() { LeaderboardId = 1, InstanceId = 2, SubLeaderboardId = 3, SubInstanceId = 4 }];

            try
            {
                manager.ExpectedSchedulePath = schedulePath;
                database.PersistInitialData(schedulePath, [], leaderboards, instances, metaEntries);

                Assert.True(manager.ScheduleExistedWhenSeeded);
                Assert.Equal(1, manager.InitialDataCalls);
                Assert.Equal(leaderboards, manager.InitialLeaderboards);
                Assert.Equal(instances, manager.InitialInstances);
                Assert.Equal(metaEntries, manager.InitialMetaEntries);
            }
            finally
            {
                File.Delete(schedulePath);
            }
        }

        [Fact]
        public void Initialize_ProviderFailure_ResetsDatabaseState()
        {
            FakeLeaderboardDBManager manager = new() { InitializeResult = false };
            LeaderboardDatabase database = new(manager);

            Assert.False(database.Initialize());
            Assert.False(database.IsInitialized);
            Assert.Null(database.DBManager);
        }

        [Fact]
        public void Initialize_MalformedScheduleWithExistingData_ResetsDatabaseState()
        {
            FakeLeaderboardDBManager manager = new() { ExistingLeaderboards = [new() { LeaderboardId = 1 }] };
            string schedulePath = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.json");
            File.WriteAllText(schedulePath, "{");
            IDBManager previousManager = IDBManager.Instance;
            IDBManager.Instance = new FakeAccountDBManager();
            LeaderboardDatabase database = new(manager, schedulePath);

            try
            {
                Assert.False(database.Initialize());
                Assert.False(database.IsInitialized);
                Assert.Null(database.DBManager);
                Assert.Equal(0, database.LeaderboardCount);
                Assert.Equal(0, manager.InitialDataCalls);
            }
            finally
            {
                IDBManager.Instance = previousManager;
                File.Delete(schedulePath);
            }
        }

        [Fact]
        public void Initialize_InitialSeedFailure_RetriesWithSameEmptyProvider()
        {
            FakeLeaderboardDBManager manager = new() { ThrowOnInitialData = true };
            string schedulePath = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.json");
            File.WriteAllText(schedulePath, "[]");
            IDBManager previousManager = IDBManager.Instance;
            IDBManager.Instance = new FakeAccountDBManager();
            TestLeaderboardDatabase database = new(manager, schedulePath);

            try
            {
                Assert.False(database.InitializePersistence(manager));
                Assert.False(database.IsInitialized);
                Assert.Null(database.DBManager);
                Assert.Equal(1, manager.InitialDataCalls);

                manager.ThrowOnInitialData = false;

                Assert.True(database.InitializePersistence(manager));
                Assert.False(database.IsInitialized);
                Assert.Same(manager, database.DBManager);
                Assert.Equal(2, manager.InitialDataCalls);
            }
            finally
            {
                IDBManager.Instance = previousManager;
                File.Delete(schedulePath);
            }
        }
    }

    internal sealed class FakeLeaderboardDBManager : ILeaderboardDBManager
    {
        public bool InitializeResult { get; set; } = true;
        public bool IsNewDatabase { get; set; }
        public bool ThrowOnInitialData { get; set; }
        public int InitialDataCalls { get; private set; }
        public bool ScheduleExistedWhenSeeded { get; private set; }
        public List<DBLeaderboard> InitialLeaderboards { get; private set; }
        public List<DBLeaderboardInstance> InitialInstances { get; private set; }
        public List<DBMetaEntry> InitialMetaEntries { get; private set; }
        public List<DBRewardEntry> Rewards { get; } = new();
        public int UpdateRewardCalls { get; private set; }
        public DBLeaderboard[] ExistingLeaderboards { get; set; } = [];

        public bool Initialize(out bool isNewDatabase) { isNewDatabase = IsNewDatabase; return InitializeResult; }
        public void InsertInitialData(List<DBLeaderboard> leaderboards, List<DBLeaderboardInstance> instances, List<DBMetaEntry> metaEntries)
        {
            ScheduleExistedWhenSeeded = File.Exists(ExpectedSchedulePath);
            InitialDataCalls++;
            InitialLeaderboards = leaderboards;
            InitialInstances = instances;
            InitialMetaEntries = metaEntries;
            if (ThrowOnInitialData)
                throw new InvalidOperationException("seed failure");
        }

        public string ExpectedSchedulePath { get; set; }
        public void InsertLeaderboards(List<DBLeaderboard> dbLeaderboards) { }
        public void UpdateLeaderboards(List<DBLeaderboard> dbLeaderboards) { }
        public DBLeaderboard[] GetLeaderboards() => ExistingLeaderboards;
        public bool UpdateActiveInstanceState(long leaderboardId, long activeInstanceId, int state) => true;
        public List<DBLeaderboardInstance> GetInstances(long leaderboardId, int maxArchivedInstances) => [];
        public DBLeaderboardInstance GetInstance(long leaderboardId, long instanceId) => null;
        public void UpdateOrInsertInstances(List<DBLeaderboardInstance> dbInstances) { }
        public void InsertInstance(DBLeaderboardInstance dbInstance) { }
        public void UpdateInstanceState(long instanceId, int state) { }
        public void UpdateInstanceActivationDate(DBLeaderboardInstance dbInstance) { }
        public List<DBLeaderboardEntry> GetEntries(long instanceId, bool ascending) => [];
        public void UpdateOrInsertEntries(List<DBLeaderboardEntry> dbEntries) { }
        public long GetSubInstanceId(long leaderboardId, long instanceId, long subLeaderboardId) => 0;
        public void InsertMetaEntries(List<DBMetaEntry> instances) { }
        public List<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId) => [];
        public void InsertRewards(List<DBRewardEntry> dbRewards) { }
        public List<DBRewardEntry> GetRewards(long participantId) => Rewards.Where(reward => reward.ParticipantId == participantId && reward.RewardedDate == 0).ToList();
        public void UpdateReward(DBRewardEntry reward) { UpdateRewardCalls++; }
    }

    internal sealed class TestLeaderboardDatabase(ILeaderboardDBManager manager, string schedulePath) : LeaderboardDatabase(manager, schedulePath)
    {
        protected override (List<DBLeaderboard>, List<DBLeaderboardInstance>, List<DBMetaEntry>, List<LeaderboardScheduler>) BuildInitialData()
        {
            return ([], [], [], []);
        }
    }

    internal sealed class FakeAccountDBManager : IDBManager
    {
        public bool Initialize() => true;
        public bool TryQueryAccountByEmail(string email, out DBAccount account) { account = null; return false; }
        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut) { playerDbId = 0; playerNameOut = null; return false; }
        public bool TryGetPlayerName(ulong playerDbId, out string playerName) { playerName = null; return false; }
        public bool GetPlayerNames(Dictionary<ulong, string> playerNames) => true;
        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime) { lastLogoutTime = 0; return false; }
        public bool InsertAccount(DBAccount account) => false;
        public bool UpdateAccount(DBAccount account) => false;
        public bool LoadPlayerData(DBAccount account) => false;
        public bool SavePlayerData(DBAccount account) => false;
        public bool LoadGuilds(List<DBGuild> guilds) => false;
        public bool SaveGuild(DBGuild guild) => false;
        public bool DeleteGuild(DBGuild guild) => false;
        public bool SaveGuildMember(DBGuildMember guildMember) => false;
        public bool DeleteGuildMember(DBGuildMember guildMember) => false;
    }
}
