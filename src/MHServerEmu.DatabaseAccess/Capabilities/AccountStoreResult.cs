namespace MHServerEmu.DatabaseAccess
{
    public enum AccountStoreResult
    {
        Success,
        AccountNotFound,
        EmailConflict,
        PlayerNameConflict,
        StaleRevision,
        InvalidData,
        Failed,
        OutcomeUncertain,
    }
}
