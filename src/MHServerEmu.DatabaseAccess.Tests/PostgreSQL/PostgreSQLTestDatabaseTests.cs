using Npgsql;

namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    public class PostgreSQLTestDatabaseTests
    {
        [PostgreSQLFact]
        public void OpenConnection_UsesTestDatabaseSchema()
        {
            using PostgreSQLTestDatabase database = new();
            using NpgsqlConnection connection = database.OpenConnection();
            using NpgsqlCommand command = new("SELECT current_schema()", connection);

            string schemaName = (string)command.ExecuteScalar();

            Assert.Equal(database.SchemaName, schemaName);
        }

        [Fact]
        public void Dispose_RetriesCleanupAndClearsPoolBeforeDroppingSchema()
        {
            List<string> steps = [];
            int dropAttempts = 0;
            PostgreSQLTestDatabase database = new(
                () => steps.Add("clear"),
                () =>
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
    }
}
