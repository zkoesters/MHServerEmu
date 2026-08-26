namespace MHServerEmu.DatabaseAccess.Tests.MySQL
{
    internal sealed class MySQLFactAttribute : FactAttribute
    {
        public const string ConnectionStringEnvironmentVariable = "MHSERVEREMU_MYSQL_TEST_CONNECTION_STRING";

        public MySQLFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable)))
            {
                Skip = $"Set {ConnectionStringEnvironmentVariable} to run MySQL integration tests.";
            }
        }
    }
}
