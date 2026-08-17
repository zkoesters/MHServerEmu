using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRewardGenerationTests
    {
        [Fact]
        public void TryBuild_DuplicateParticipant_IsInvalid()
        {
            DBRewardEntry[] candidates =
            [
                new DBRewardEntry(1, 2, 3, 42, 1),
                new DBRewardEntry(1, 2, 4, 42, 2),
            ];

            Assert.False(LeaderboardRewardBuilder.TryBuild(candidates, out _));
        }
    }
}
