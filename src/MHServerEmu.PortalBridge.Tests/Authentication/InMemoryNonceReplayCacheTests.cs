using MHServerEmu.PortalBridge.Authentication;

namespace MHServerEmu.PortalBridge.Tests.Authentication
{
    public class InMemoryNonceReplayCacheTests
    {
        [Fact]
        public void TryReserve_DuplicateWithinRetention_RejectsSecondReservation()
        {
            InMemoryNonceReplayCache cache = new();
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1710000000);

            Assert.True(cache.TryReserve("00112233445566778899aabbccddeeff", now, TimeSpan.FromMinutes(5)));
            Assert.False(cache.TryReserve("00112233445566778899aabbccddeeff", now.AddMinutes(4), TimeSpan.FromMinutes(5)));
            Assert.True(cache.TryReserve("00112233445566778899aabbccddeeff", now.AddMinutes(5), TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void TryReserve_ExpiredEntryAtCurrentTime_IsRemovedBeforeDuplicateCheck()
        {
            InMemoryNonceReplayCache cache = new();
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1710000000);

            Assert.True(cache.TryReserve("00112233445566778899aabbccddeeff", now, TimeSpan.Zero));
            Assert.True(cache.TryReserve("00112233445566778899aabbccddeeff", now, TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void TryReserve_ConcurrentDuplicate_OnlyOneReservationSucceeds()
        {
            InMemoryNonceReplayCache cache = new();
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1710000000);
            int accepted = 0;

            Parallel.For(0, 32, _ =>
            {
                if (cache.TryReserve("00112233445566778899aabbccddeeff", now, TimeSpan.FromMinutes(5)))
                    Interlocked.Increment(ref accepted);
            });

            Assert.Equal(1, accepted);
        }

        [Fact]
        public void TryReserve_CapacityReached_RejectsNewNonce()
        {
            InMemoryNonceReplayCache cache = new();
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1710000000);

            for (int index = 0; index < InMemoryNonceReplayCache.Capacity; index++)
                Assert.True(cache.TryReserve(CreateNonce(index), now, TimeSpan.FromMinutes(5)));

            Assert.False(cache.TryReserve(CreateNonce(InMemoryNonceReplayCache.Capacity), now, TimeSpan.FromMinutes(5)));
        }

        [Fact]
        public void TryReserve_ExpiredCapacityEntries_AllowNewNonce()
        {
            InMemoryNonceReplayCache cache = new();
            DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1710000000);

            for (int index = 0; index < InMemoryNonceReplayCache.Capacity; index++)
                Assert.True(cache.TryReserve(CreateNonce(index), now, TimeSpan.FromMinutes(5)));

            Assert.True(cache.TryReserve(CreateNonce(InMemoryNonceReplayCache.Capacity), now.AddMinutes(5), TimeSpan.FromMinutes(5)));
        }

        private static string CreateNonce(int value)
        {
            return value.ToString("x32");
        }
    }
}
