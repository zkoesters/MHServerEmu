namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public static class LeaderboardInstanceIdGenerator
    {
        private const ulong UpperBitsMask = 0xFFFFFFFF00000000UL;
        private const ulong LowerBitsMask = 0x00000000FFFFFFFFUL;

        public static bool TryGetNext(long leaderboardId, IEnumerable<long> existingInstanceIds, out long instanceId)
        {
            instanceId = 0;
            if (existingInstanceIds == null)
                return false;

            ulong leaderboardBits = unchecked((ulong)leaderboardId);
            ulong upperBits = leaderboardBits & UpperBitsMask;
            uint greatestCounter = 0;
            HashSet<ulong> existing = new();

            foreach (long existingInstanceId in existingInstanceIds)
            {
                ulong existingBits = unchecked((ulong)existingInstanceId);
                if ((existingBits & UpperBitsMask) != upperBits || !existing.Add(existingBits))
                    return false;

                greatestCounter = Math.Max(greatestCounter, unchecked((uint)(existingBits & LowerBitsMask)));
            }

            if (greatestCounter == uint.MaxValue)
                return false;

            ulong nextBits = upperBits | (ulong)(greatestCounter + 1);
            if (existing.Contains(nextBits))
                return false;

            instanceId = unchecked((long)nextBits);
            return true;
        }
    }
}
