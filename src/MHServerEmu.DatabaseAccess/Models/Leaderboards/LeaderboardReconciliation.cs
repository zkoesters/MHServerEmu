namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardReconciliation
    {
        public IReadOnlyList<LeaderboardDefinitionSpec> DesiredDefinitions { get; }
        public IReadOnlyList<LeaderboardInstanceSpec> InitialInstances { get; }
        public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; }
        public long CurrentTime { get; }
        public int NormalArchiveLimit { get; }

        public LeaderboardReconciliation(IEnumerable<LeaderboardDefinitionSpec> desiredDefinitions, IEnumerable<LeaderboardInstanceSpec> initialInstances, IEnumerable<LeaderboardMetaMapping> metaMappings, long currentTime, int normalArchiveLimit)
        {
            DesiredDefinitions = LeaderboardStoreRecords.Copy(desiredDefinitions, nameof(desiredDefinitions));
            InitialInstances = LeaderboardStoreRecords.Copy(initialInstances, nameof(initialInstances));
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
            CurrentTime = currentTime;
            LeaderboardStoreRecords.RequireNonNegative(normalArchiveLimit, nameof(normalArchiveLimit));
            NormalArchiveLimit = normalArchiveLimit;
        }

        public LeaderboardReconciliation(IEnumerable<DBLeaderboard> desiredDefinitions, IEnumerable<DBLeaderboardInstance> initialInstances, IEnumerable<DBMetaEntry> metaMappings, long currentTime, int normalArchiveLimit)
            : this(
                LeaderboardStoreRecords.Convert(desiredDefinitions, LeaderboardDefinitionSpec.From, nameof(desiredDefinitions)),
                LeaderboardStoreRecords.Convert(initialInstances, LeaderboardInstanceSpec.From, nameof(initialInstances)),
                LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)),
                currentTime,
                normalArchiveLimit)
        {
        }
    }
}
