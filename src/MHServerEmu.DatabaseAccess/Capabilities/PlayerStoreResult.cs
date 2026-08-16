namespace MHServerEmu.DatabaseAccess
{
    public enum PlayerStoreResult
    {
        Success,
        AccountNotFound,
        StaleRevision,
        InvalidAggregate,
        Failed,
        OutcomeUncertain,
    }
}
