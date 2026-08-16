using MHServerEmu.Core.Config;
using MHServerEmu.Core.Logging;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.PlayerManagement;

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

        private static bool TryCreate(Func<(bool Success, PersistenceServices Services)> factory, out PersistenceServices services)
        {
            ArgumentNullException.ThrowIfNull(factory);

            (bool success, PersistenceServices createdServices) = factory();
            services = createdServices;
            return success;
        }

        private static (bool Success, PersistenceServices Services) CreateJsonServices()
        {
            JsonDBManager manager = JsonDBManager.Instance;
            if (manager.Initialize() == false)
                return (false, null);

            return (true, new(manager, manager, manager, PersistenceCapabilities.Json));
        }

        private static (bool Success, PersistenceServices Services) CreateSQLiteServices()
        {
            SQLiteDBManager manager = SQLiteDBManager.Instance;
            if (manager.Initialize() == false)
                return (false, null);

            return (true, new(manager, manager, manager, PersistenceCapabilities.SQLite));
        }
    }
}
