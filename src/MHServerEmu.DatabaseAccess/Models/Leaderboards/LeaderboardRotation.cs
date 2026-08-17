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
            LeaderboardId = leaderboardId;
            ExpectedActiveInstanceId = expectedActiveInstanceId;
            ExpectedActiveState = expectedActiveState;
            PreviousState = previousState;
            NextInstance = nextInstance ?? throw new ArgumentNullException(nameof(nextInstance));
            NextState = nextState;
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
        }

        public LeaderboardRotation(long leaderboardId, long expectedActiveInstanceId, LeaderboardState expectedActiveState, LeaderboardState previousState, DBLeaderboardInstance nextInstance, LeaderboardState nextState, IEnumerable<DBMetaEntry> metaMappings)
            : this(leaderboardId, expectedActiveInstanceId, expectedActiveState, previousState, LeaderboardInstanceSpec.From(nextInstance), nextState, LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }
    }
}
