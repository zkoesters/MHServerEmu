namespace MHServerEmu.Leaderboards.Administration
{
    public sealed class LeaderboardSummary
    {
        public long Id { get; }
        public string Name { get; }
        public bool IsEnabled { get; }
        public DateTime StartTime { get; }
        public LeaderboardInstanceSummary ActiveInstance { get; }
        public string Details { get; }

        public LeaderboardSummary(long id, string name, bool isEnabled, DateTime startTime, LeaderboardInstanceSummary activeInstance, string details)
        {
            Id = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            IsEnabled = isEnabled;
            StartTime = startTime;
            ActiveInstance = activeInstance;
            Details = details ?? throw new ArgumentNullException(nameof(details));
        }
    }
}
