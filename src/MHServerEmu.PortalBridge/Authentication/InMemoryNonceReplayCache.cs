namespace MHServerEmu.PortalBridge.Authentication
{
    public sealed class InMemoryNonceReplayCache : INonceReplayCache
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, DateTimeOffset> _reservations = new(StringComparer.Ordinal);

        public bool TryReserve(string nonce, DateTimeOffset nowUtc, TimeSpan retention)
        {
            lock (_lock)
            {
                foreach (string expiredNonce in _reservations
                    .Where(entry => entry.Value <= nowUtc)
                    .Select(entry => entry.Key)
                    .ToArray())
                {
                    _reservations.Remove(expiredNonce);
                }

                if (_reservations.ContainsKey(nonce))
                    return false;

                _reservations.Add(nonce, nowUtc.Add(retention));
                return true;
            }
        }
    }
}
