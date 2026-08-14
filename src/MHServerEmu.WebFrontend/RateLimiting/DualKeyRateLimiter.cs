using MHServerEmu.Core.RateLimiting;

namespace MHServerEmu.WebFrontend.RateLimiting
{
    internal class DualKeyRateLimiter
    {
        private readonly TimeLeakyBucketCollection<string> _sourceLimiter;
        private readonly TimeLeakyBucketCollection<string> _accountLimiter;

        public DualKeyRateLimiter(TimeSpan cost, int burst, int maxKeys)
        {
            _sourceLimiter = new(cost, burst, maxKeys);
            _accountLimiter = new(cost, burst, maxKeys);
        }

        public bool TryAdd(string sourceKey, string accountKey)
        {
            return TryAddSource(sourceKey) && TryAddAccount(accountKey);
        }

        public bool TryAddSource(string sourceKey)
        {
            return string.IsNullOrWhiteSpace(sourceKey) == false && _sourceLimiter.AddTime(sourceKey);
        }

        public bool TryAddAccount(string accountKey)
        {
            return string.IsNullOrWhiteSpace(accountKey) == false && _accountLimiter.AddTime(accountKey);
        }
    }
}
