namespace MHServerEmu.DatabaseAccess
{
    public static class LeaderboardStoreValidator
    {
        public static void ValidateVisibleInstancesLimit(int limit)
        {
            if (limit < 1 || limit > 100)
                throw new ArgumentOutOfRangeException(nameof(limit), "Visible instance limit must be between 1 and 100.");
        }
    }
}
