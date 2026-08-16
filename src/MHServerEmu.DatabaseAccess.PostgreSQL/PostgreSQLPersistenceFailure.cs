namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal sealed class PostgreSQLPersistenceFailure
    {
        public PostgreSQLPersistenceFailure(string code, string operation, string sqlState = null, string migrationIdentity = null, string entityId = null)
        {
            Code = code;
            Operation = operation;
            SqlState = sqlState;
            MigrationIdentity = migrationIdentity;
            EntityId = entityId;
        }

        public string Code { get; }
        public string Operation { get; }
        public string SqlState { get; }
        public string MigrationIdentity { get; }
        public string EntityId { get; }

        public override string ToString()
        {
            return $"Code={Code}, Operation={Operation}, SqlState={SqlState}, MigrationIdentity={MigrationIdentity}, EntityId={EntityId}";
        }
    }
}
