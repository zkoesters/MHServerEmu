namespace MHServerEmu.DatabaseAccess
{
    public enum LeaderboardStoreResult
    {
        Success,
        NotFound,
        Conflict,
        StaleState,
        InvalidData,
        Failed,
        OutcomeUncertain,
    }
}
