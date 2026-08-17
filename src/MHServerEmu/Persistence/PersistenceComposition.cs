using MHServerEmu.Core.Config;
using MHServerEmu.Core.Helpers;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.PostgreSQL.Configuration;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.PlayerManagement;
using MHServerEmu.Leaderboards;

namespace MHServerEmu.Persistence
{
    public static class PersistenceComposition
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        public static bool TryCreate(out PersistenceServices services)
        {
            PersistenceConfig persistenceConfig = ConfigManager.Instance.GetConfig<PersistenceConfig>();
            PlayerManagerConfig playerManagerConfig = ConfigManager.Instance.GetConfig<PlayerManagerConfig>();
            if (PersistenceProviderSelector.TrySelect(persistenceConfig.Provider, playerManagerConfig.UseJsonDBManager, out PersistenceProvider provider, out string error) == false)
            {
                Logger.Error($"TryCreate(): {error}");
                services = null;
                return false;
            }

            return TryCreate(provider, CreateJsonServices, CreateSQLiteServices, out services);
        }

        public static Task<PersistenceRuntime> CreateAsync(Action<PersistenceFatalFailure> fatalCallback = null, CancellationToken cancellationToken = default)
        {
            fatalCallback ??= _ => { };

            PersistenceConfig persistenceConfig = ConfigManager.Instance.GetConfig<PersistenceConfig>();
            PlayerManagerConfig playerManagerConfig = ConfigManager.Instance.GetConfig<PlayerManagerConfig>();
            if (PersistenceProviderSelector.TrySelect(persistenceConfig.Provider, playerManagerConfig.UseJsonDBManager, out PersistenceProvider provider, out string error) == false)
                return Task.FromException<PersistenceRuntime>(new InvalidOperationException(error));

            if (provider == PersistenceProvider.PostgreSQL && ConfigManager.Instance.HasUnsafeUnixOverrideFilePermissions())
                return Task.FromException<PersistenceRuntime>(new InvalidOperationException("PostgreSQL persistence startup failed: UnsafeOverrideFilePermissions."));

            return CreateAsync(
                provider,
                () => CreateRuntimeAsync(CreateJsonServices),
                () => CreateRuntimeAsync(CreateSQLiteServices),
                () => PostgreSQLPersistenceFacade.StartAsync(
                    ConfigManager.Instance.GetConfig<PostgreSQLConfig>(),
                    ConfigManager.Instance.GetOverrideString("PostgreSQL", "ConnectionString"),
                    fatalCallback,
                    cancellationToken));
        }

        internal static bool TryCreate(PersistenceProvider provider, Func<(bool Success, PersistenceServices Services)> jsonFactory,
            Func<(bool Success, PersistenceServices Services)> sqliteFactory, out PersistenceServices services)
        {
            services = null;

            switch (provider)
            {
                case PersistenceProvider.Json:
                    return TryCreate(jsonFactory, out services);

                case PersistenceProvider.SQLite:
                    return TryCreate(sqliteFactory, out services);

                case PersistenceProvider.PostgreSQL:
                    Logger.Error("TryCreate(): PostgreSQL persistence is not available.");
                    return false;

                default:
                    Logger.Error($"TryCreate(): Unsupported persistence provider {provider}.");
                    return false;
            }
        }

        internal static Task<PersistenceRuntime> CreateAsync(PersistenceProvider provider,
            Func<Task<PersistenceRuntime>> jsonFactory,
            Func<Task<PersistenceRuntime>> sqliteFactory,
            Func<Task<PersistenceRuntime>> postgreSQLFactory)
        {
            ArgumentNullException.ThrowIfNull(jsonFactory);
            ArgumentNullException.ThrowIfNull(sqliteFactory);
            ArgumentNullException.ThrowIfNull(postgreSQLFactory);

            return provider switch
            {
                PersistenceProvider.Json => jsonFactory(),
                PersistenceProvider.SQLite => sqliteFactory(),
                PersistenceProvider.PostgreSQL => postgreSQLFactory(),
                _ => Task.FromException<PersistenceRuntime>(new InvalidOperationException($"Unsupported persistence provider {provider}.")),
            };
        }

        private static bool TryCreate(Func<(bool Success, PersistenceServices Services)> factory, out PersistenceServices services)
        {
            ArgumentNullException.ThrowIfNull(factory);

            (bool success, PersistenceServices createdServices) = factory();
            services = createdServices;
            return success;
        }

        private static Task<PersistenceRuntime> CreateRuntimeAsync(Func<(bool Success, PersistenceServices Services)> factory)
        {
            if (TryCreate(factory, out PersistenceServices services) == false)
                return Task.FromException<PersistenceRuntime>(new InvalidOperationException("Persistence initialization failed."));

            return Task.FromResult(new PersistenceRuntime(services, () => ValueTask.CompletedTask));
        }

        private static (bool Success, PersistenceServices Services) CreateJsonServices()
        {
            JsonDBManager manager = JsonDBManager.Instance;
            if (manager.Initialize() == false)
                return (false, null);

            return (true, new(manager, manager, manager, CreateLeaderboardStore(), PersistenceCapabilities.Json));
        }

        private static (bool Success, PersistenceServices Services) CreateSQLiteServices()
        {
            SQLiteDBManager manager = SQLiteDBManager.Instance;
            if (manager.Initialize() == false)
                return (false, null);

            return (true, new(manager, manager, manager, CreateLeaderboardStore(), PersistenceCapabilities.SQLite));
        }

        private static SQLiteLeaderboardDBManager CreateLeaderboardStore()
        {
            LeaderboardsConfig config = ConfigManager.Instance.GetConfig<LeaderboardsConfig>();
            return new SQLiteLeaderboardDBManager(Path.Combine(FileHelper.DataDirectory, "Leaderboards", config.DatabaseFile));
        }
    }
}
