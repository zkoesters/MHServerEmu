using MHServerEmu.DatabaseAccess.SQLite;
using MHServerEmu.DatabaseAccess.PostgreSQL;

namespace MHServerEmu.DatabaseAccess
{
    public enum LeaderboardDBManagerType
    {
        SQLite,
        PostgreSQL
    }

    public static class LeaderboardDBManagerFactory
    {
        public static bool TryResolveType(string configuredType, out LeaderboardDBManagerType type)
        {
            configuredType = configuredType?.Trim();
            if (string.IsNullOrEmpty(configuredType)
                || string.Equals(configuredType, nameof(LeaderboardDBManagerType.SQLite), StringComparison.OrdinalIgnoreCase))
            {
                type = LeaderboardDBManagerType.SQLite;
                return true;
            }

            if (string.Equals(configuredType, nameof(LeaderboardDBManagerType.PostgreSQL), StringComparison.OrdinalIgnoreCase))
            {
                type = LeaderboardDBManagerType.PostgreSQL;
                return true;
            }

            type = default;
            return false;
        }

        public static bool TryCreate(string configuredType, string databasePath, out ILeaderboardDBManager manager)
        {
            manager = null;
            if (!TryResolveType(configuredType, out LeaderboardDBManagerType type))
                return false;

            manager = type switch
            {
                LeaderboardDBManagerType.SQLite => new SQLiteLeaderboardDBManager(databasePath),
                LeaderboardDBManagerType.PostgreSQL => new PostgreSQLLeaderboardDBManager(),
                _ => null
            };
            return manager != null;
        }
    }
}
