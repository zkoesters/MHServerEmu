using Gazillion;

namespace MHServerEmu.Leaderboards.Administration
{
    public sealed class LeaderboardInstanceSummary
    {
        public long Id { get; }
        public long LeaderboardId { get; }
        public string LeaderboardName { get; }
        public LeaderboardState State { get; }
        public DateTime ActivationTime { get; }
        public DateTime ExpirationTime { get; }
        public string Details { get; }

        public LeaderboardInstanceSummary(long id, long leaderboardId, string leaderboardName, LeaderboardState state,
            DateTime activationTime, DateTime expirationTime, string details)
        {
            Id = id;
            LeaderboardId = leaderboardId;
            LeaderboardName = leaderboardName ?? throw new ArgumentNullException(nameof(leaderboardName));
            State = state;
            ActivationTime = activationTime;
            ExpirationTime = expirationTime;
            Details = details ?? throw new ArgumentNullException(nameof(details));
        }
    }
}
