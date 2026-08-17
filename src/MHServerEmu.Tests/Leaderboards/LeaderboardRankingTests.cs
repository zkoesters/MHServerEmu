using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRankingTests
    {
        [Fact]
        public void GetCompetitionRank_FirstZeroScoreAndTie_UseRankOne()
        {
            Assert.Equal(1, LeaderboardRanking.GetCompetitionRank(entryIndex: 0, score: 0, hasPrevious: false, previousScore: 0, previousRank: 0));
            Assert.Equal(1, LeaderboardRanking.GetCompetitionRank(entryIndex: 1, score: 0, hasPrevious: true, previousScore: 0, previousRank: 1));
            Assert.Equal(3, LeaderboardRanking.GetCompetitionRank(entryIndex: 2, score: 1, hasPrevious: true, previousScore: 0, previousRank: 1));
        }
    }
}
