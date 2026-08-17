using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardRewardGeneration
    {
        public long LeaderboardId { get; }
        public long ExpectedActiveInstanceId { get; }
        public long InstanceId { get; }
        public LeaderboardState ExpectedState { get; }
        public IReadOnlyList<LeaderboardRewardWrite> Rewards { get; }

        public LeaderboardRewardGeneration(long leaderboardId, long expectedActiveInstanceId, long instanceId, LeaderboardState expectedState, IEnumerable<LeaderboardRewardWrite> rewards)
        {
            if (expectedState != LeaderboardState.eLBS_Expired)
                throw new ArgumentException("Expected state must be expired.", nameof(expectedState));

            LeaderboardId = leaderboardId;
            ExpectedActiveInstanceId = expectedActiveInstanceId;
            InstanceId = instanceId;
            ExpectedState = expectedState;
            Rewards = LeaderboardStoreRecords.Copy(rewards, nameof(rewards));
            LeaderboardStoreRecords.RequireSameRewardScope(Rewards, leaderboardId, instanceId, nameof(rewards));
        }

        public LeaderboardRewardGeneration(long leaderboardId, long expectedActiveInstanceId, long instanceId, LeaderboardState expectedState, IEnumerable<DBRewardEntry> rewards)
            : this(leaderboardId, expectedActiveInstanceId, instanceId, expectedState, LeaderboardStoreRecords.Convert(rewards, LeaderboardRewardWrite.From, nameof(rewards)))
        {
        }
    }
}
