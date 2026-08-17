namespace MHServerEmu.DatabaseAccess
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class LeaderboardPaginationContractAttribute : Attribute
    {
        public int MinimumLimit => 1;
        public int MaximumLimit => 100;
        public bool RequiresValidationBeforeConnection => true;
        public LeaderboardStoreResult InvalidLimitResult => LeaderboardStoreResult.InvalidData;
        public bool EmptyOutputOnInvalidLimit => true;
    }
}
