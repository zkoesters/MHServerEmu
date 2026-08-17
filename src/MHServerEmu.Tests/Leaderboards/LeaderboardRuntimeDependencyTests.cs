namespace MHServerEmu.Tests.Leaderboards
{
    public class LeaderboardRuntimeDependencyTests
    {
        [Fact]
        public void RuntimeEntities_DoNotReferenceCompatibilitySingletons()
        {
            string root = FindRoot();
            string[] paths =
            [
                "src/MHServerEmu.Leaderboards/Leaderboard.cs",
                "src/MHServerEmu.Leaderboards/LeaderboardInstance.cs",
                "src/MHServerEmu.Leaderboards/LeaderboardEntry.cs",
                "src/MHServerEmu.Leaderboards/MetaLeaderboardEntry.cs",
                "src/MHServerEmu.Leaderboards/LeaderboardRewardManager.cs",
            ];

            string[] offenders = paths.Where(path =>
                File.ReadAllText(Path.Combine(root, path)).Contains("LeaderboardDatabase" + ".Instance", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains(".DBManager", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains("SQLiteLeaderboardDBManager" + ".Instance", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains("ServerManager" + ".Instance", StringComparison.Ordinal)).ToArray();

            Assert.Empty(offenders);
        }

        [Fact]
        public void ProductionLeaderboardRuntime_DoesNotReferenceCompatibilitySingletons()
        {
            string root = FindRoot();
            string[] paths =
            [
                "src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs",
                "src/MHServerEmu.Leaderboards/LeaderboardService.cs",
                "src/MHServerEmu/Commands/Implementations/LeaderboardsCommands.cs",
                "src/MHServerEmu.DatabaseAccess/SQLite/SQLiteLeaderboardDBManager.cs",
            ];

            string[] offenders = paths.Where(path =>
                File.ReadAllText(Path.Combine(root, path)).Contains("LeaderboardDatabase" + ".Instance", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains("SQLiteLeaderboardDBManager" + ".Instance", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains("static LeaderboardDatabase Instance", StringComparison.Ordinal)
                || File.ReadAllText(Path.Combine(root, path)).Contains("static SQLiteLeaderboardDBManager Instance", StringComparison.Ordinal)).ToArray();

            Assert.Empty(offenders);
        }

        [Fact]
        public void LeaderboardInstance_DoesNotReadStaticConfiguration()
        {
            string root = FindRoot();

            Assert.DoesNotContain("ConfigManager" + ".Instance", File.ReadAllText(Path.Combine(root, "src/MHServerEmu.Leaderboards/LeaderboardInstance.cs")), StringComparison.Ordinal);
        }

        [Fact]
        public void ClientLookups_LoadArchivedInstancesOnDemand()
        {
            string source = File.ReadAllText(Path.Combine(FindRoot(), "src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs"));

            Assert.Equal(2, source.Split("leaderboard.GetInstance(instanceId, true)", StringSplitOptions.None).Length - 1);
        }

        [Fact]
        public void LifecycleTick_DrainsAcceptedScoresBeforeExpiringInstances()
        {
            string source = File.ReadAllText(Path.Combine(FindRoot(), "src/MHServerEmu.Leaderboards/LeaderboardDatabase.cs"));
            int drainScores = source.IndexOf("ProcessLeaderboardScoreUpdateQueue();", StringComparison.Ordinal);
            int updateLifecycle = source.IndexOf("leaderboard.UpdateState(updateTime);", StringComparison.Ordinal);

            Assert.InRange(drainScores, 0, updateLifecycle - 1);
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
