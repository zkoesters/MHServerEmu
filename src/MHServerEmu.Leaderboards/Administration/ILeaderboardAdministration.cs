namespace MHServerEmu.Leaderboards.Administration
{
    public interface ILeaderboardAdministration
    {
        LeaderboardAdminResult ReloadSchedule();
        LeaderboardAdminResult TryGetInstance(long instanceId, out LeaderboardInstanceSummary summary);
        LeaderboardAdminResult TryGetLeaderboard(long leaderboardId, out LeaderboardSummary summary);
        LeaderboardAdminResult GetLeaderboards(out IReadOnlyList<LeaderboardSummary> summaries);
    }
}
