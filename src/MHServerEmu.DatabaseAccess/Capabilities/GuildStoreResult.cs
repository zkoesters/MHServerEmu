namespace MHServerEmu.DatabaseAccess
{
    public enum GuildStoreResult
    {
        Success,
        GuildNotFound,
        NameConflict,
        MembershipConflict,
        StaleRevision,
        InvalidData,
        Failed,
        OutcomeUncertain,
    }
}
