namespace MHServerEmu.DatabaseAccess.PostgreSQL.Migrations
{
    internal sealed class PostgreSQLMigration
    {
        internal PostgreSQLMigration(int version, string name, string checksum, string sql)
        {
            Version = version;
            Name = name;
            Checksum = checksum;
            Sql = sql;
        }

        internal int Version { get; }
        internal string Name { get; }
        internal string Checksum { get; }
        internal string Sql { get; }
    }
}
