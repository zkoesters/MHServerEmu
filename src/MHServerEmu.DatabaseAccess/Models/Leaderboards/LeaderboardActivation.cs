using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardActivation
    {
        public long LeaderboardId { get; }
        public long ExpectedActiveInstanceId { get; }
        public long InstanceId { get; }
        public LeaderboardState ExpectedState { get; }

        public LeaderboardActivation(long leaderboardId, long expectedActiveInstanceId, long instanceId, LeaderboardState expectedState)
        {
            if (expectedState != LeaderboardState.eLBS_Created)
                throw new ArgumentException("Expected state must be created.", nameof(expectedState));

            LeaderboardId = leaderboardId;
            ExpectedActiveInstanceId = expectedActiveInstanceId;
            InstanceId = instanceId;
            ExpectedState = expectedState;
        }

        public LeaderboardActivation(long leaderboardId, long expectedActiveInstanceId, long instanceId)
            : this(leaderboardId, expectedActiveInstanceId, instanceId, LeaderboardState.eLBS_Created)
        {
        }
    }
}
