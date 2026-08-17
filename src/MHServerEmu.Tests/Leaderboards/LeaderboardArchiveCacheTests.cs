using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardArchiveCacheTests
    {
        [Fact]
        public void RuntimeOptions_ClampsArchiveCacheCapacityToOne()
        {
            LeaderboardRuntimeOptions options = new("schedule.json", normalArchiveLimit: 0);

            Assert.Equal(1, options.ArchiveCacheCapacity);
        }
    }
}
