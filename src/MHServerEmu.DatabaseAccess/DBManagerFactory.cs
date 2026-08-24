namespace MHServerEmu.DatabaseAccess
{
    public enum DBManagerType
    {
        Json,
        SQLite,
        PostgreSQL
    }

    public static class DBManagerFactory
    {
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
