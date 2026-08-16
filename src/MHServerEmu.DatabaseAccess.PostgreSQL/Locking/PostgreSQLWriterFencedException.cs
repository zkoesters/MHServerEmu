namespace MHServerEmu.DatabaseAccess.PostgreSQL.Locking
{
    internal sealed class PostgreSQLWriterFencedException : Exception
    {
        internal PostgreSQLWriterFencedException()
            : base("The PostgreSQL writer owner is fenced.")
        {
        }
    }
}
