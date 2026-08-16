namespace MHServerEmu.DatabaseAccess.PostgreSQL.Migrations
{
    internal sealed class PostgreSQLMigrationResult
    {
        private PostgreSQLMigrationResult(bool succeeded, int appliedMigrationCount, PostgreSQLPersistenceFailure failure)
        {
            Succeeded = succeeded;
            AppliedMigrationCount = appliedMigrationCount;
            Failure = failure;
        }

        internal bool Succeeded { get; }
        internal int AppliedMigrationCount { get; }
        internal PostgreSQLPersistenceFailure Failure { get; }

        internal static PostgreSQLMigrationResult Success(int appliedMigrationCount)
        {
            return new PostgreSQLMigrationResult(true, appliedMigrationCount, null);
        }

        internal static PostgreSQLMigrationResult Failed(PostgreSQLPersistenceFailure failure)
        {
            return new PostgreSQLMigrationResult(false, 0, failure);
        }
    }
}
