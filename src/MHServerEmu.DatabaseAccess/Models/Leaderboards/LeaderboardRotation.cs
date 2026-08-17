using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardRotation
    {
        public long LeaderboardId { get; }
        public long ExpectedActiveInstanceId { get; }
        public LeaderboardState ExpectedActiveState { get; }
        public LeaderboardState PreviousState { get; }
        public LeaderboardInstanceSpec NextInstance { get; }
        public LeaderboardState NextState { get; }
        public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; }

        public LeaderboardRotation(long leaderboardId, long expectedActiveInstanceId, LeaderboardState expectedActiveState, LeaderboardState previousState, LeaderboardInstanceSpec nextInstance, LeaderboardState nextState, IEnumerable<LeaderboardMetaMapping> metaMappings)
        {
            if (expectedActiveState != LeaderboardState.eLBS_Expired)
                throw new ArgumentException("Expected active state must be expired.", nameof(expectedActiveState));

            if (previousState != LeaderboardState.eLBS_Expired)
                throw new ArgumentException("Previous state must remain expired for reward generation.", nameof(previousState));

            if (nextState != LeaderboardState.eLBS_Created)
                throw new ArgumentException("Next state must be created.", nameof(nextState));

            LeaderboardId = leaderboardId;
            ExpectedActiveInstanceId = expectedActiveInstanceId;
            ExpectedActiveState = expectedActiveState;
            PreviousState = previousState;
            NextInstance = nextInstance ?? throw new ArgumentNullException(nameof(nextInstance));
            if (NextInstance.LeaderboardId != LeaderboardId)
                throw new ArgumentException("Next instance must belong to the requested leaderboard.", nameof(nextInstance));

            if (NextInstance.State != NextState)
                throw new ArgumentException("Next instance state must match the requested next state.", nameof(nextInstance));

            NextState = nextState;
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
            if (MetaMappings.Any(mapping => mapping.LeaderboardId != LeaderboardId || mapping.InstanceId != NextInstance.InstanceId))
                throw new ArgumentException("Meta mappings must belong to the requested next instance.", nameof(metaMappings));

            if (MetaMappings.GroupBy(mapping => mapping.SubLeaderboardId).Any(group => group.Skip(1).Any()))
                throw new ArgumentException("Meta mappings cannot contain duplicate sub-leaderboards.", nameof(metaMappings));
        }

        public LeaderboardRotation(long leaderboardId, long expectedActiveInstanceId, LeaderboardState expectedActiveState, LeaderboardState previousState, DBLeaderboardInstance nextInstance, LeaderboardState nextState, IEnumerable<DBMetaEntry> metaMappings)
            : this(leaderboardId, expectedActiveInstanceId, expectedActiveState, previousState, LeaderboardInstanceSpec.From(nextInstance), nextState, LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }
    }
}
