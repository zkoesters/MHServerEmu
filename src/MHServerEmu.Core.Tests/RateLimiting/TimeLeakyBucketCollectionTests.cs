using System.Collections.Concurrent;
using MHServerEmu.Core.RateLimiting;

namespace MHServerEmu.Core.Tests.RateLimiting
{
    public class TimeLeakyBucketCollectionTests
    {
        [Fact]
        public void AddTime_AllowsConfiguredBurstBeforeRejecting()
        {
            TimeLeakyBucketCollection<string> limiter = new(TimeSpan.FromSeconds(1), 3, 10, () => TimeSpan.Zero);

            Assert.True(limiter.AddTime("key"));
            Assert.True(limiter.AddTime("key"));
            Assert.True(limiter.AddTime("key"));
            Assert.False(limiter.AddTime("key"));
        }

        [Fact]
        public void AddTime_AllowsRequestAfterCostHasElapsed()
        {
            TimeSpan now = TimeSpan.Zero;
            TimeLeakyBucketCollection<string> limiter = new(TimeSpan.FromSeconds(1), 1, 10, () => now);

            Assert.True(limiter.AddTime("key"));
            Assert.False(limiter.AddTime("key"));

            now = TimeSpan.FromSeconds(1);

            Assert.True(limiter.AddTime("key"));
        }

        [Fact]
        public void AddTime_RejectsNewKeysAtCapacityUntilDrainedEntriesAreRemoved()
        {
            TimeSpan now = TimeSpan.Zero;
            TimeLeakyBucketCollection<string> limiter = new(TimeSpan.FromSeconds(1), 1, 2, () => now);

            Assert.True(limiter.AddTime("first"));
            Assert.True(limiter.AddTime("second"));
            Assert.False(limiter.AddTime("third"));
            Assert.Equal(2, limiter.Count);

            now = TimeSpan.FromSeconds(1);

            Assert.True(limiter.AddTime("third"));
            Assert.Equal(1, limiter.Count);
        }

        [Fact]
        public void AddTime_IsSafeUnderConcurrentRequests()
        {
            TimeLeakyBucketCollection<string> limiter = new(TimeSpan.FromSeconds(1), 100, 10, () => TimeSpan.Zero);
            ConcurrentBag<bool> results = new();

            Parallel.For(0, 200, _ => results.Add(limiter.AddTime("key")));

            Assert.Equal(100, results.Count(result => result));
            Assert.Equal(100, results.Count(result => result == false));
        }
    }
}
