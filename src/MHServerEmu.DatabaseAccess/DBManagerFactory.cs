using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.MySQL;
using MHServerEmu.DatabaseAccess.PostgreSQL;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.DatabaseAccess
{
    public enum DBManagerType
    {
        Json,
        SQLite,
        MySQL,
        PostgreSQL
    }

    public static class DBManagerFactory
    {
        public static bool TryCreate(
            string configuredType,
            bool useJsonDBManager,
            out IDBManager manager,
            out bool usedLegacyJsonSetting)
        {
            manager = null;

            if (TryResolveType(configuredType, useJsonDBManager, out DBManagerType type, out usedLegacyJsonSetting) == false)
                return false;

            manager = type switch
            {
                DBManagerType.Json => JsonDBManager.Instance,
                DBManagerType.SQLite => SQLiteDBManager.Instance,
                DBManagerType.MySQL => MySQLDBManager.Instance,
                DBManagerType.PostgreSQL => PostgreSQLDBManager.Instance,
                _ => null
            };

            return manager != null;
        }

        public static bool TryResolveType(
            string configuredType,
            bool useJsonDBManager,
            out DBManagerType type,
            out bool usedLegacyJsonSetting)
        {
            configuredType = configuredType?.Trim();

            if (string.IsNullOrEmpty(configuredType))
            {
                type = useJsonDBManager ? DBManagerType.Json : DBManagerType.SQLite;
                usedLegacyJsonSetting = useJsonDBManager;
                return true;
            }

            if (!Enum.TryParse(configuredType, true, out type)
                || !Enum.IsDefined(type)
                || !string.Equals(configuredType, type.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                type = DBManagerType.SQLite;
                usedLegacyJsonSetting = false;
                return false;
            }

            if (type == DBManagerType.SQLite && useJsonDBManager)
            {
                type = DBManagerType.Json;
                usedLegacyJsonSetting = true;
                return true;
            }

            usedLegacyJsonSetting = false;
            return true;
        }
    }
}
