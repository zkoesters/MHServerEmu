using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal static class PostgreSQLDataSourceFactory
    {
        internal static NpgsqlDataSource Build(PostgreSQLSettings settings)
        {
            NpgsqlDataSourceBuilder builder = new(settings.ConnectionString);
            return builder.Build();
        }

        internal static NpgsqlConnection BuildWriterConnection(PostgreSQLSettings settings)
        {
            return new NpgsqlConnection(settings.CreateWriterConnectionString());
        }
    }
}
