using MHServerEmu.Core.Config;

namespace MHServerEmu.Leaderboards
{
    public class LeaderboardsConfig : ConfigContainer
    {
        public string DatabaseType { get; private set; } = string.Empty;
        public string DatabaseFile { get; private set; } = "Leaderboards.db";
        public string ScheduleFile { get; private set; } = "LeaderboardSchedule.json";
        public int AutoSaveIntervalMinutes { get; private set; } = 5;
    }
}
