namespace MHServerEmu.Leaderboards
{
    public sealed class LeaderboardRuntimeOptions
    {
        public string SchedulePath { get; }
        public int NormalArchiveLimit { get; }
        public int ArchiveCacheCapacity { get; }
        public int AutoSaveIntervalMinutes { get; }

        public LeaderboardRuntimeOptions(string schedulePath, int normalArchiveLimit, int autoSaveIntervalMinutes = 5)
        {
            if (string.IsNullOrWhiteSpace(schedulePath))
                throw new ArgumentException("Schedule path is required.", nameof(schedulePath));

            SchedulePath = schedulePath;
            NormalArchiveLimit = Math.Max(0, normalArchiveLimit);
            ArchiveCacheCapacity = Math.Max(1, normalArchiveLimit);
            AutoSaveIntervalMinutes = Math.Max(1, autoSaveIntervalMinutes);
        }
    }
}
