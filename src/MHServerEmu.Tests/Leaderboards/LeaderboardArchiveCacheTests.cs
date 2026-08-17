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

        [Fact]
        public void ArchiveCache_EvictsLeastRecentlyUsedEntryAtConfiguredCapacity()
        {
            LeaderboardArchiveCache<string> cache = new(capacity: 2);
            cache.Set(1, "one");
            cache.Set(2, "two");
            Assert.Equal("one", cache.Get(1));
            cache.Set(3, "three");

            Assert.False(cache.TryGet(2, out _));
            Assert.Equal("one", cache.Get(1));
            Assert.Equal("three", cache.Get(3));
        }
    }
}
