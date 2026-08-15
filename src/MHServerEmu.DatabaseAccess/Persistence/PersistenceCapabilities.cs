namespace MHServerEmu.DatabaseAccess.Persistence
{
    public sealed class PersistenceCapabilities
    {
        public static PersistenceCapabilities Json { get; } = new(false, false, false);
        public static PersistenceCapabilities SQLite { get; } = new(true, true, false);
        public static PersistenceCapabilities PostgreSQL { get; } = new(true, true, true);

        public bool VerifyAccountCredentials { get; }
        public bool VerifyClientTokens { get; }
        public bool HasDurableSecurityVersions { get; }

        public PersistenceCapabilities(bool verifyAccountCredentials, bool verifyClientTokens, bool hasDurableSecurityVersions)
        {
            VerifyAccountCredentials = verifyAccountCredentials;
            VerifyClientTokens = verifyClientTokens;
            HasDurableSecurityVersions = hasDurableSecurityVersions;
        }
    }
}
