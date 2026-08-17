using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardShutdownDrainTests
    {
        [Fact]
        public async Task DisabledService_RemainsRunningUntilShutdown()
        {
            LeaderboardService service = new(JsonDBManager.Instance, new SQLiteLeaderboardDBManager("unused.db"));
            ServerManager manager = new();
            manager.RegisterGameService(service, GameServiceType.Leaderboard);

            Assert.True(manager.RunServices());
            Assert.Equal(GameServiceState.Running, service.State);
            Assert.False(manager.WaitForFaultAsync().IsCompleted);

            await Task.Run(manager.ShutdownServices).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public void Shutdown_DrainsAcceptedScoresAndSavesBeforeServiceStops()
        {
            string source = File.ReadAllText(Path.Combine(FindRoot(), "src/MHServerEmu.Leaderboards/LeaderboardService.cs"));
            int drain = source.IndexOf("_database.ProcessLeaderboardScoreUpdateQueue();", StringComparison.Ordinal);
            int save = source.IndexOf("_database.Save();", drain, StringComparison.Ordinal);
            int shutdown = source.IndexOf("State = GameServiceState.Shutdown;", save, StringComparison.Ordinal);

            Assert.InRange(drain, 0, save - 1);
            Assert.InRange(save, 0, shutdown - 1);
        }

        private static string FindRoot()
        {
            DirectoryInfo directory = new(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "MHServerEmu.sln")))
                    return directory.FullName;
                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
