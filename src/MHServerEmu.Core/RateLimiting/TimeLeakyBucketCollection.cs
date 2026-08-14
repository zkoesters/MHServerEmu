using System.Diagnostics;
using MHServerEmu.Core.System.Time;

namespace MHServerEmu.Core.RateLimiting
{
    public class TimeLeakyBucketCollection<TKey>
    {
        // See here for reference: https://www.codeofhonor.com/blog/using-transaction-rate-limiting-to-improve-service-reliability

        private readonly Dictionary<TKey, TimeSpan> _dict = new();
        private readonly Func<TimeSpan> _clock;

        private readonly TimeSpan _cost;
        private readonly TimeSpan _maxCost;
        private readonly int _maxKeys;

        public TimeLeakyBucketCollection(TimeSpan cost, int burst)
            : this(cost, burst, int.MaxValue)
        {
        }

        public TimeLeakyBucketCollection(TimeSpan cost, int burst, int maxKeys)
            : this(cost, burst, maxKeys, () => Clock.ElapsedTime)
        {
        }

        internal TimeLeakyBucketCollection(TimeSpan cost, int burst, int maxKeys, Func<TimeSpan> clock)
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(cost, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(burst, 0);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxKeys, 0);

            _cost = cost;
            _maxCost = TimeSpan.FromTicks(checked(cost.Ticks * (long)burst));
            _maxKeys = maxKeys;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));

            Debug.Assert(_cost <= _maxCost);
        }

        public bool AddTime(TKey key)
        {
            lock (_dict)
            {
                TimeSpan currentTime = _clock();
                RemoveDrainedEntries(currentTime);

                if (_dict.TryGetValue(key, out TimeSpan time) == false)
                {
                    if (_dict.Count >= _maxKeys)
                        return false;

                    time = currentTime;
                }

                TimeSpan newTime = time + _cost;
                if (newTime - currentTime > _maxCost)
                    return false;

                _dict[key] = newTime;
                return true;
            }
        }

        public void Reset()
        {
            lock (_dict)
                _dict.Clear();
        }

        internal int Count
        {
            get
            {
                lock (_dict)
                    return _dict.Count;
            }
        }

        private void RemoveDrainedEntries(TimeSpan currentTime)
        {
            List<TKey> drainedKeys = null;
            foreach ((TKey key, TimeSpan time) in _dict)
            {
                if (time <= currentTime)
                {
                    drainedKeys ??= new();
                    drainedKeys.Add(key);
                }
            }

            if (drainedKeys == null)
                return;

            foreach (TKey key in drainedKeys)
                _dict.Remove(key);
        }
    }
}
