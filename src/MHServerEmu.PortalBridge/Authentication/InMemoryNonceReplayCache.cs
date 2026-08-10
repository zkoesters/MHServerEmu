namespace MHServerEmu.PortalBridge.Authentication
{
    public sealed class InMemoryNonceReplayCache : INonceReplayCache
    {
        internal const int Capacity = 10_000;

        private readonly object _lock = new();
        private readonly Dictionary<string, long> _reservations = new(StringComparer.Ordinal);
        private readonly PriorityQueue<(string Nonce, long ExpiresAtTicks), long> _expirations = new();

        public bool TryReserve(string nonce, DateTimeOffset nowUtc, TimeSpan retention)
        {
            lock (_lock)
            {
                long nowTicks = nowUtc.UtcDateTime.Ticks;
                RemoveExpired(nowTicks);

                if (_reservations.ContainsKey(nonce))
                    return false;
                if (_reservations.Count >= Capacity)
                    return false;

                long expiresAtTicks = nowUtc.Add(retention).UtcDateTime.Ticks;
                _reservations.Add(nonce, expiresAtTicks);
                _expirations.Enqueue((nonce, expiresAtTicks), expiresAtTicks);
                return true;
            }
        }

        private void RemoveExpired(long nowTicks)
        {
            while (_expirations.TryPeek(out var expired, out long expiresAtTicks) && expiresAtTicks <= nowTicks)
            {
                _expirations.Dequeue();
                if (_reservations.TryGetValue(expired.Nonce, out long reservationExpiry) &&
                    reservationExpiry == expired.ExpiresAtTicks)
                    _reservations.Remove(expired.Nonce);
            }
        }
    }
}
