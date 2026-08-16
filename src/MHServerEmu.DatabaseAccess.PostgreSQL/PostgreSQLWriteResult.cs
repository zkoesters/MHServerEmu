namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal readonly record struct PostgreSQLWriteResult(PostgreSQLWriteOutcome Outcome, PostgreSQLPersistenceFailure Failure)
    {
        internal static PostgreSQLWriteResult Success() => new(PostgreSQLWriteOutcome.Success, null);
    }
}
