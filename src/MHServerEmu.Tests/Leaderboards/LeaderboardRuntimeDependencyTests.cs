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
        public void LeaderboardInstance_DoesNotReadStaticConfiguration()
        {
            string root = FindRoot();

            Assert.DoesNotContain("ConfigManager" + ".Instance", File.ReadAllText(Path.Combine(root, "src/MHServerEmu.Leaderboards/LeaderboardInstance.cs")), StringComparison.Ordinal);
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
