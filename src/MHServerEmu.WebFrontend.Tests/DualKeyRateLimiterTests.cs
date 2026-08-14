using MHServerEmu.WebFrontend.RateLimiting;

namespace MHServerEmu.WebFrontend.Tests
{
    public class DualKeyRateLimiterTests
    {
        [Fact]
        public void TryAdd_AllowsIndependentSourceAndAccountKeys()
        {
            DualKeyRateLimiter limiter = new(TimeSpan.FromMinutes(1), 1, 10);

            Assert.True(limiter.TryAdd("192.0.2.1", "first@example.com"));
            Assert.False(limiter.TryAddSource("192.0.2.1"));
            Assert.False(limiter.TryAddAccount("first@example.com"));
            Assert.True(limiter.TryAdd("192.0.2.2", "second@example.com"));
        }
    }
}
