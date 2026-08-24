using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLConnectionInfo
    {
        private PostgreSQLConnectionInfo(NpgsqlConnectionStringBuilder connectionStringBuilder)
        {
            ConnectionString = connectionStringBuilder.ConnectionString;
            Description = $"{connectionStringBuilder.Host}:{connectionStringBuilder.Port}/{connectionStringBuilder.Database}";
        }

        public string ConnectionString { get; }
        public string Description { get; }

        public static bool TryParse(string connectionString, out PostgreSQLConnectionInfo connectionInfo, out string error)
        {
            connectionInfo = null;

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                error = "PostgreSQL connection string is required.";
                return false;
            }

            try
            {
                NpgsqlConnectionStringBuilder connectionStringBuilder = new(connectionString);
                if (!connectionStringBuilder.ContainsKey("Host") || string.IsNullOrWhiteSpace(connectionStringBuilder.Host)
                    || !connectionStringBuilder.ContainsKey("Database") || string.IsNullOrWhiteSpace(connectionStringBuilder.Database))
                {
                    error = "PostgreSQL connection string must include Host and Database.";
                    return false;
                }

                connectionInfo = new PostgreSQLConnectionInfo(connectionStringBuilder);
                error = null;
                return true;
            }
            catch (ArgumentException)
            {
                error = "PostgreSQL connection string is invalid.";
                return false;
            }
        }
    }
}
