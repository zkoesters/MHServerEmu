namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardVisibilityRequest
    {
        public long LeaderboardId { get; }
        public int ArchiveLimit { get; }
        public long CurrentTime { get; }

        public LeaderboardVisibilityRequest(long leaderboardId, int archiveLimit, long currentTime)
        {
            LeaderboardStoreRecords.RequireNonNegative(archiveLimit, nameof(archiveLimit));
            LeaderboardId = leaderboardId;
            ArchiveLimit = archiveLimit;
            CurrentTime = currentTime;
        }
    }
}
