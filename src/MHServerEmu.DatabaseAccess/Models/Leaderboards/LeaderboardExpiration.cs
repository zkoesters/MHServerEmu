using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardExpiration
    {
        public long LeaderboardId { get; }
        public long ExpectedActiveInstanceId { get; }
        public long InstanceId { get; }
        public LeaderboardState ExpectedState { get; }
        public IReadOnlyList<LeaderboardEntryWrite> Entries { get; }

        public LeaderboardExpiration(long leaderboardId, long expectedActiveInstanceId, long instanceId, LeaderboardState expectedState, IEnumerable<LeaderboardEntryWrite> entries)
        {
            if (expectedState != LeaderboardState.eLBS_Active)
                throw new ArgumentException("Expected state must be active.", nameof(expectedState));

            LeaderboardId = leaderboardId;
            ExpectedActiveInstanceId = expectedActiveInstanceId;
            InstanceId = instanceId;
            ExpectedState = expectedState;
            Entries = LeaderboardStoreRecords.Copy(entries, nameof(entries));
            LeaderboardStoreRecords.RequireSameInstance(Entries, instanceId, nameof(entries));
        }

        public LeaderboardExpiration(long leaderboardId, long expectedActiveInstanceId, long instanceId, LeaderboardState expectedState, IEnumerable<DBLeaderboardEntry> entries)
            : this(leaderboardId, expectedActiveInstanceId, instanceId, expectedState, LeaderboardStoreRecords.Convert(entries, LeaderboardEntryWrite.From, nameof(entries)))
        {
        }
    }
}
