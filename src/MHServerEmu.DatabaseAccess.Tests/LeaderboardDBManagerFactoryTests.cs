using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.MySQL;
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
        [InlineData("MySQL")]
        [InlineData("mysql")]
        [InlineData(" MySql ")]
        public void TryResolveType_MySQLConfiguration_ReturnsMySQL(string configuredType)
        {
            Assert.True(LeaderboardDBManagerFactory.TryResolveType(configuredType, out LeaderboardDBManagerType type));
            Assert.Equal(LeaderboardDBManagerType.MySQL, type);
        }

        [Theory]
        [InlineData("PostgreSQL")]
        [InlineData("postgresql")]
        [InlineData(" PostgreSQL ")]
        public void TryResolveType_PostgreSQLConfiguration_ReturnsPostgreSQL(string configuredType)
        {
            Assert.True(LeaderboardDBManagerFactory.TryResolveType(configuredType, out LeaderboardDBManagerType type));
            Assert.Equal(LeaderboardDBManagerType.PostgreSQL, type);
        }

        [Theory]
        [InlineData("Json")]
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

        [Fact]
        public void TryCreate_PostgreSQLConfiguration_ConstructsFreshManagerWithoutDatabasePathOrConfiguration()
        {
            Assert.True(LeaderboardDBManagerFactory.TryCreate("PostgreSQL", string.Empty, out ILeaderboardDBManager first));
            Assert.True(LeaderboardDBManagerFactory.TryCreate("PostgreSQL", string.Empty, out ILeaderboardDBManager second));

            Assert.IsType<PostgreSQLLeaderboardDBManager>(first);
            Assert.IsType<PostgreSQLLeaderboardDBManager>(second);
            Assert.NotSame(first, second);
        }

        [Fact]
        public void TryCreate_MySQLConfiguration_IgnoresDatabasePathAndConstructsDistinctManagers()
        {
            Assert.True(LeaderboardDBManagerFactory.TryCreate("MySQL", "first-sqlite-path.db", out ILeaderboardDBManager first));
            Assert.True(LeaderboardDBManagerFactory.TryCreate("MySQL", "second-sqlite-path.db", out ILeaderboardDBManager second));

            Assert.IsType<MySQLLeaderboardDBManager>(first);
            Assert.IsType<MySQLLeaderboardDBManager>(second);
            Assert.NotSame(first, second);
        }
    }
}
