using Npgsql;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    internal interface IPostgreSQLTransactionCommitter
    {
        Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken);
    }

    internal sealed class NpgsqlTransactionCommitter : IPostgreSQLTransactionCommitter
    {
        public Task CommitAsync(NpgsqlTransaction transaction, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            return transaction.CommitAsync(cancellationToken);
        }
    }
}
