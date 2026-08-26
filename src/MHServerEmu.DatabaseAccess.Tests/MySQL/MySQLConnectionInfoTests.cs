using MHServerEmu.DatabaseAccess.MySQL;
using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLConnectionInfoTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void TryParse_RequiredConnectionString_ReturnsRequiredError(string connectionString)
        {
            bool parsed = MySQLConnectionInfo.TryParse(connectionString, out _, out string error);

            Assert.False(parsed);
            Assert.Equal("MySQL connection string is required.", error);
        }

        [Theory]
        [InlineData("Server=mysql.example")]
        public void TryParse_ConnectionStringMissingServerOrDatabase_ReturnsValidationError(string connectionString)
        {
            bool parsed = MySQLConnectionInfo.TryParse(connectionString, out _, out string error);

            Assert.False(parsed);
            Assert.Equal("MySQL connection string must include Server and Database.", error);
        }

        [Fact]
        public void TryParse_ConnectionStringWithoutServer_ReturnsValidationError()
        {
            bool parsed = MySQLConnectionInfo.TryParse("Database=mhserveremu", out _, out string error);

            Assert.False(parsed);
            Assert.Equal("MySQL connection string must include Server and Database.", error);
        }

        [Fact]
        public void TryParse_ConnectionStringWithHostAlias_ReturnsNormalizedConnection()
        {
            bool parsed = MySQLConnectionInfo.TryParse("Host=mysql.example;Database=mhserveremu", out MySQLConnectionInfo info, out _);

            Assert.True(parsed);
            Assert.Equal("mysql.example:3306/mhserveremu", info.Description);
        }

        [Fact]
        public void TryParse_MalformedConnectionString_RedactsValuesFromError()
        {
            bool parsed = MySQLConnectionInfo.TryParse(
                "Server=mysql.example;Password=do-not-log;Port=not-a-number",
                out _,
                out string error);

            Assert.False(parsed);
            Assert.Equal("MySQL connection string is invalid.", error);
            Assert.False(error.Contains("mysql.example", StringComparison.Ordinal));
            Assert.False(error.Contains("do-not-log", StringComparison.Ordinal));
        }

        [Fact]
        public void TryParse_UnknownKeyword_RedactsEndpointAndDatabaseFromError()
        {
            bool parsed = MySQLConnectionInfo.TryParse(
                "Server=database.example;Database=mhserveremu;InvalidKeyword=value",
                out _,
                out string error);

            Assert.False(parsed);
            Assert.Equal("MySQL connection string is invalid.", error);
            Assert.False(error.Contains("database.example", StringComparison.Ordinal));
            Assert.False(error.Contains("mhserveremu", StringComparison.Ordinal));
        }

        [Fact]
        public void TryParse_ValidConnectionString_ReturnsNormalizedConnectionAndRedactedDescription()
        {
            const string connectionString = "Server=mysql.example;Port=3307;Database=mhserveremu;User ID=dbuser;Password=do-not-log";

            bool parsed = MySQLConnectionInfo.TryParse(connectionString, out MySQLConnectionInfo info, out _);

            Assert.True(parsed);
            Assert.Equal(new MySqlConnectionStringBuilder(connectionString).ConnectionString, info.ConnectionString);
            Assert.Equal("mysql.example:3307/mhserveremu", info.Description);
            Assert.False(info.Description.Contains("dbuser", StringComparison.Ordinal));
            Assert.False(info.Description.Contains("do-not-log", StringComparison.Ordinal));
        }

        [Fact]
        public void TryParse_ConnectionStringWithoutPort_UsesDefaultPortInDescription()
        {
            bool parsed = MySQLConnectionInfo.TryParse("Server=mysql.example;Database=mhserveremu", out MySQLConnectionInfo info, out _);

            Assert.True(parsed);
            Assert.Equal("mysql.example:3306/mhserveremu", info.Description);
        }
    }
}
