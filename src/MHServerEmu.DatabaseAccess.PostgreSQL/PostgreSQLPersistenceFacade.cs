using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.PostgreSQL.Migrations;

namespace MHServerEmu.DatabaseAccess.PostgreSQL
{
    public static class PostgreSQLPersistenceFacade
    {
        public static async Task<PersistenceRuntime> StartAsync(PostgreSQLConfig config, string connectionString,
            Action<PersistenceFatalFailure> fatalCallback, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(config);
            ArgumentNullException.ThrowIfNull(fatalCallback);

            if (PostgreSQLSettings.TryCreate(connectionString, config, false, out PostgreSQLSettings settings, out PostgreSQLPersistenceFailure settingsFailure) == false)
                throw CreateStartException(settingsFailure);

            PostgreSQLProvider provider = new(settings, PostgreSQLMigrationCatalog.LoadEmbedded(), fatalCallback: failure => fatalCallback(new(failure.Code, failure.Operation)));
            try
            {
                PostgreSQLProviderStartResult result = await provider.StartAsync(cancellationToken);
                if (result.Succeeded == false)
                    throw CreateStartException(result.Failure);

                PersistenceServices services = new(
                    new PostgreSQLAccountStore(provider.DataSource, provider.StoreExecutor),
                    new PostgreSQLPlayerStore(provider.DataSource, provider.StoreExecutor),
                    new PostgreSQLGuildStore(provider.DataSource, provider.StoreExecutor),
                    new PostgreSQLLeaderboardStore(provider.DataSource, provider.StoreExecutor),
                    PersistenceCapabilities.PostgreSQL);
                return new PersistenceRuntime(services, provider.DisposeAsync);
            }
            catch
            {
                await provider.DisposeAsync();
                throw;
            }
        }

        private static InvalidOperationException CreateStartException(PostgreSQLPersistenceFailure failure)
        {
            return new InvalidOperationException($"PostgreSQL persistence startup failed: {failure.Code}.");
        }
    }
}
