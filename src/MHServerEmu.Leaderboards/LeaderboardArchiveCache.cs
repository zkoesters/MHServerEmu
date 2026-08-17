namespace MHServerEmu.Leaderboards
{
    public sealed class LeaderboardArchiveCache<T>
    {
        private readonly int _capacity;
        private readonly Dictionary<ulong, LinkedListNode<(ulong Key, T Value)>> _entries = new();
        private readonly LinkedList<(ulong Key, T Value)> _recency = new();

        public LeaderboardArchiveCache(int capacity)
        {
            _capacity = Math.Max(1, capacity);
        }

        public bool TryGet(ulong key, out T value)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(ulong Key, T Value)> node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default;
            return false;
        }

        public T Get(ulong key)
        {
            if (TryGet(key, out T value) == false)
                throw new KeyNotFoundException();
            return value;
        }

        public void Set(ulong key, T value)
        {
            if (_entries.TryGetValue(key, out LinkedListNode<(ulong Key, T Value)> existing))
            {
                existing.Value = (key, value);
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return;
            }

            LinkedListNode<(ulong Key, T Value)> node = _recency.AddFirst((key, value));
            _entries.Add(key, node);
            if (_entries.Count <= _capacity)
                return;

            LinkedListNode<(ulong Key, T Value)> leastRecent = _recency.Last;
            _entries.Remove(leastRecent.Value.Key);
            _recency.RemoveLast();
        }
    }
}
