using MySqlConnector;

namespace MHServerEmu.DatabaseAccess.MySQL
{
    internal sealed class MySQLConnectionInfo
    {
        private MySQLConnectionInfo(MySqlConnectionStringBuilder connectionStringBuilder)
        {
            ConnectionString = connectionStringBuilder.ConnectionString;
            Description = $"{connectionStringBuilder.Server}:{connectionStringBuilder.Port}/{connectionStringBuilder.Database}";
        }

        public string ConnectionString { get; }
        public string Description { get; }

        public static bool TryParse(string connectionString, out MySQLConnectionInfo connectionInfo, out string error)
        {
            connectionInfo = null;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                error = "MySQL connection string is required.";
                return false;
            }

            try
            {
                MySqlConnectionStringBuilder connectionStringBuilder = new(connectionString);
                if (!connectionStringBuilder.ContainsKey("Server") || string.IsNullOrWhiteSpace(connectionStringBuilder.Server)
                    || !connectionStringBuilder.ContainsKey("Database") || string.IsNullOrWhiteSpace(connectionStringBuilder.Database))
                {
                    error = "MySQL connection string must include Server and Database.";
                    return false;
                }

                connectionInfo = new MySQLConnectionInfo(connectionStringBuilder);
                error = null;
                return true;
            }
            catch (ArgumentException)
            {
                error = "MySQL connection string is invalid.";
                return false;
            }
        }
    }
}
