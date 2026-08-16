namespace MHServerEmu.DatabaseAccess.PostgreSQL.Migrations
{
    internal sealed class PostgreSQLMigrationResource
    {
        public PostgreSQLMigrationResource(string name, byte[] bytes)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Bytes = bytes?.ToArray() ?? throw new ArgumentNullException(nameof(bytes));
        }

        internal string Name { get; }
        internal byte[] Bytes { get; }
    }
}
