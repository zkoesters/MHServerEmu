namespace MHServerEmu.DatabaseAccess.PostgreSQL.Locking
{
    internal static class PostgreSQLAdvisoryKeys
    {
        internal const int Namespace = 0x4D485345;
        internal const int WriterResource = 1;
        internal const int MigrationResource = 2;
    }
}
