using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.DatabaseAccess.Tests
{
    public class LeaderboardDBManagerFactoryTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("SQLite")]
        [InlineData("sqlite")]
        [InlineData(" SQLite ")]
        public void TryResolveType_SQLiteConfiguration_ReturnsSQLite(string configuredType)
        {
            Assert.True(LeaderboardDBManagerFactory.TryResolveType(configuredType, out LeaderboardDBManagerType type));
            Assert.Equal(LeaderboardDBManagerType.SQLite, type);
        }

        [Theory]
        [InlineData("Json")]
        [InlineData("PostgreSQL")]
        [InlineData("Oracle")]
        [InlineData("0")]
        public void TryResolveType_UnavailableConfiguration_ReturnsFalse(string configuredType)
        {
            Assert.False(LeaderboardDBManagerFactory.TryResolveType(configuredType, out _));
        }

        [Fact]
        public void TryCreate_SQLiteConfiguration_ConstructsConfiguredManager()
        {
            string path = Path.Combine(Path.GetTempPath(), $"leaderboards-{Guid.NewGuid():N}.db");

            try
            {
                Assert.True(LeaderboardDBManagerFactory.TryCreate("SQLite", path, out ILeaderboardDBManager manager));
                Assert.IsType<SQLiteLeaderboardDBManager>(manager);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
