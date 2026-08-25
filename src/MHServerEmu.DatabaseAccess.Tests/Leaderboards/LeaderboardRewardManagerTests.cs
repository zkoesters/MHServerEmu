using MHServerEmu.DatabaseAccess.Models.Leaderboards;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.DatabaseAccess.Tests.Leaderboards
{
    public class LeaderboardRewardManagerTests
    {
        [Fact]
        public void QueryAndFinalize_UseInjectedProvider()
        {
            FakeLeaderboardDBManager manager = new();
            DBRewardEntry reward = new(1, 2, 3, 4, 5);
            manager.Rewards.Add(reward);
            LeaderboardRewardManager rewardManager = new(manager);

            Assert.True(rewardManager.QueryRewards(4));
            Assert.True(rewardManager.FinalizeReward(1, 2, 4));
            Assert.Equal(1, manager.UpdateRewardCalls);
        }
    }
}
