namespace MHServerEmu.DatabaseAccess.Tests.PostgreSQL
{
    internal sealed class PostgreSQLFactAttribute : FactAttribute
    {
        public const string ConnectionStringEnvironmentVariable = "MHSERVEREMU_POSTGRES_TEST_CONNECTION_STRING";

        public PostgreSQLFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
            {
                Skip = $"Set {ConnectionStringEnvironmentVariable} to run PostgreSQL integration tests.";
            }
        }
    }
}
