namespace MHServerEmu.DatabaseAccess
{
    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class LeaderboardStoreOutputContractAttribute : Attribute
    {
        public bool ReturnsDetachedRows => true;
        public bool CopiesRuleStates => true;
        public bool EmptyOutputsOnFailure => true;
    }
}
