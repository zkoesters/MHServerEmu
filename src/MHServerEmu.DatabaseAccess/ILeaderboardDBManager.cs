using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess
{
    public interface ILeaderboardDBManager
    {
        bool Initialize(out bool isNewDatabase);
        void InsertInitialData(List<DBLeaderboard> dbLeaderboards, List<DBLeaderboardInstance> dbInstances, List<DBMetaEntry> dbMetaEntries);
        void InsertLeaderboards(List<DBLeaderboard> dbLeaderboards);
        void UpdateLeaderboards(List<DBLeaderboard> dbLeaderboards);
        DBLeaderboard[] GetLeaderboards();
        bool UpdateActiveInstanceState(long leaderboardId, long activeInstanceId, int state);
        List<DBLeaderboardInstance> GetInstances(long leaderboardId, int maxArchivedInstances);
        DBLeaderboardInstance GetInstance(long leaderboardId, long instanceId);
        void UpdateOrInsertInstances(List<DBLeaderboardInstance> dbInstances);
        void InsertInstance(DBLeaderboardInstance dbInstance);
        void UpdateInstanceState(long instanceId, int state);
        void UpdateInstanceActivationDate(DBLeaderboardInstance dbInstance);
        List<DBLeaderboardEntry> GetEntries(long instanceId, bool ascending);
        void UpdateOrInsertEntries(List<DBLeaderboardEntry> dbEntries);
        long GetSubInstanceId(long leaderboardId, long instanceId, long subLeaderboardId);
        void InsertMetaEntries(List<DBMetaEntry> instances);
        List<DBMetaEntry> GetMetaEntries(long leaderboardId, long instanceId);
        void InsertRewards(List<DBRewardEntry> dbRewards);
        List<DBRewardEntry> GetRewards(long participantId);
        void UpdateReward(DBRewardEntry reward);
    }
}
