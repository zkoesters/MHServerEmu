using MHServerEmu.Commands.Implementations;
using MHServerEmu.Leaderboards.Administration;

namespace MHServerEmu.Tests.Commands
{
    public class LeaderboardsCommandsTests
    {
        [Fact]
        public void All_UsesInjectedFacade()
        {
            StubLeaderboardAdministration facade = new();

            string output = new LeaderboardsCommands(facade).All(Array.Empty<string>(), null);

            Assert.Contains(facade.Summary.Name, output);
        }

        private sealed class StubLeaderboardAdministration : ILeaderboardAdministration
        {
            public LeaderboardSummary Summary { get; } = new(1, "Injected leaderboard", true, DateTime.UnixEpoch, null, "");

            public LeaderboardAdminResult ReloadSchedule() => LeaderboardAdminResult.Success;
            public LeaderboardAdminResult TryGetInstance(long instanceId, out LeaderboardInstanceSummary summary) { summary = null; return LeaderboardAdminResult.NotFound; }
            public LeaderboardAdminResult TryGetLeaderboard(long leaderboardId, out LeaderboardSummary summary) { summary = null; return LeaderboardAdminResult.NotFound; }
            public LeaderboardAdminResult GetLeaderboards(out IReadOnlyList<LeaderboardSummary> summaries) { summaries = [Summary]; return LeaderboardAdminResult.Success; }
        }
    }
}
