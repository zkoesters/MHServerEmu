namespace MHServerEmu.DatabaseAccess.Persistence
{
    public static class PersistenceProviderSelector
    {
        public static bool TrySelect(string configuredProvider, bool useLegacyJson, out PersistenceProvider provider, out string error)
        {
            if (string.IsNullOrWhiteSpace(configuredProvider))
            {
                provider = useLegacyJson ? PersistenceProvider.Json : PersistenceProvider.SQLite;
                error = string.Empty;
                return true;
            }

            provider = default;
            if (useLegacyJson)
            {
                error = "Persistence provider selection conflicts with legacy JSON persistence.";
                return false;
            }

            string providerName = configuredProvider.Trim();
            if (Enum.TryParse(providerName, true, out PersistenceProvider parsedProvider) &&
                Enum.IsDefined(parsedProvider) &&
                string.Equals(Enum.GetName(parsedProvider), providerName, StringComparison.OrdinalIgnoreCase))
            {
                provider = parsedProvider;
                error = string.Empty;
                return true;
            }

            error = "The configured persistence provider is not supported.";
            return false;
        }
    }
}
