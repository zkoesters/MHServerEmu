namespace MHServerEmu.DatabaseAccess.PostgreSQL.Locking
{
    internal sealed class PostgreSQLWriterFenceToken
    {
        internal PostgreSQLWriterFenceToken(Guid ownerId, long generation)
        {
            OwnerId = ownerId;
            Generation = generation;
        }

        internal Guid OwnerId { get; }
        internal long Generation { get; }
    }
}
