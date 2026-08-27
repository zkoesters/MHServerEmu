using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    public class MySQLTestDatabaseTests
    {
        [Fact]
        public void Constructor_CreatesLowercaseTemporaryDatabaseAndScopesConnectionString()
        {
            List<string> createdDatabaseNames = [];
            using MySQLTestDatabase database = new(
                "Server=mysql.example;Port=3307;Database=admin;User ID=test_user;Password=test_password",
                createdDatabaseNames.Add,
                () => { },
                _ => { });
            MySqlConnectionStringBuilder connectionStringBuilder = new(database.ConnectionString);

            Assert.Single(createdDatabaseNames);
            Assert.Equal(database.DatabaseName, createdDatabaseNames[0]);
            Assert.Matches("^mhserveremu_test_[0-9a-f]{32}$", database.DatabaseName);
            Assert.Equal(database.DatabaseName, connectionStringBuilder.Database);
            Assert.Equal("mysql.example", connectionStringBuilder.Server);
            Assert.Equal(3307u, connectionStringBuilder.Port);
        }

        [Fact]
        public void QuoteIdentifier_UsesBackticksAndEscapesBackticks()
        {
            Assert.Equal("`mh``serveremu`", MySQLTestDatabase.QuoteIdentifier("mh`serveremu"));
        }

        [Fact]
        public void Dispose_RetriesCleanupAndClearsScopedPoolBeforeDroppingDatabase()
        {
            List<string> steps = [];
            int dropAttempts = 0;
            MySQLTestDatabase database = new(
                "Server=mysql.example;Database=admin",
                _ => { },
                () => steps.Add("clear"),
                _ =>
                {
                    steps.Add("drop");
                    if (++dropAttempts == 1)
                    {
                        throw new InvalidOperationException();
                    }
                });

            Assert.Throws<InvalidOperationException>(database.Dispose);

            database.Dispose();
            database.Dispose();

            Assert.Equal(["clear", "drop", "clear", "drop"], steps);
        }

        [MySQLFact]
        public void OpenConnection_UsesTemporaryDatabase()
        {
            using MySQLTestDatabase database = new();
            using MySqlConnection connection = database.OpenConnection();
            using MySqlCommand command = new("SELECT DATABASE()", connection);

            string databaseName = (string)command.ExecuteScalar();

            Assert.Equal(database.DatabaseName, databaseName);
        }
    }
}
