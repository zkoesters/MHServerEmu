using MHServerEmu.DatabaseAccess.PostgreSQL;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLConnectionInfoTests
    {
        [Fact]
        public void TryParse_EmptyConnectionString_ReturnsRequiredError()
        {
            bool parsed = PostgreSQLConnectionInfo.TryParse(string.Empty, out _, out string error);

            Assert.False(parsed);
            Assert.Equal("PostgreSQL connection string is required.", error);
        }

        [Fact]
        public void TryParse_MalformedConnectionString_RedactsPasswordFromError()
        {
            bool parsed = PostgreSQLConnectionInfo.TryParse(
                "Host=postgres.example;Password=do-not-log;Port=not-a-number",
                out _,
                out string error);

            Assert.False(parsed);
            Assert.Equal("PostgreSQL connection string is invalid.", error);
            Assert.False(error.Contains("do-not-log", StringComparison.Ordinal));
        }

        [Fact]
        public void TryParse_ValidConnectionString_ReturnsNormalizedConnectionAndRedactedDescription()
        {
            const string connectionString = "Host=postgres.example;Port=5433;Database=mhserveremu;Username=dbuser;Password=do-not-log";

            bool parsed = PostgreSQLConnectionInfo.TryParse(connectionString, out PostgreSQLConnectionInfo info, out _);

            Assert.True(parsed);
            Assert.Equal(new NpgsqlConnectionStringBuilder(connectionString).ConnectionString, info.ConnectionString);
            Assert.Equal("postgres.example:5433/mhserveremu", info.Description);
            Assert.False(info.Description.Contains("dbuser", StringComparison.Ordinal));
            Assert.False(info.Description.Contains("do-not-log", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("Database=mhserveremu")]
        [InlineData("Host=postgres.example")]
        public void TryParse_ConnectionStringMissingHostOrDatabase_ReturnsValidationError(string connectionString)
        {
            bool parsed = PostgreSQLConnectionInfo.TryParse(connectionString, out _, out string error);

            Assert.False(parsed);
            Assert.Equal("PostgreSQL connection string must include Host and Database.", error);
        }
    }
}
