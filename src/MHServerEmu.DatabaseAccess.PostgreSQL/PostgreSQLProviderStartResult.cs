namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLProviderStartResult
    {
        private PostgreSQLProviderStartResult(PostgreSQLPersistenceFailure failure)
        {
            Failure = failure;
        }

        internal bool Succeeded => Failure == null;
        internal PostgreSQLPersistenceFailure Failure { get; }

        internal static PostgreSQLProviderStartResult Success() => new(null);
        internal static PostgreSQLProviderStartResult Failed(PostgreSQLPersistenceFailure failure) => new(failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}
