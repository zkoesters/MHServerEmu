namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public readonly record struct LeaderboardRewardKey(long LeaderboardId, long InstanceId, long ParticipantId);
}
