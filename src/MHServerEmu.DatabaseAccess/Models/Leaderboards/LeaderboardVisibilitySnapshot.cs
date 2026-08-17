namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardVisibilitySnapshot
    {
        public IReadOnlyList<LeaderboardInstanceSpec> NormalArchiveInstances { get; }
        public IReadOnlyList<LeaderboardInstanceSpec> ChangedInstances { get; }
        public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; }

        public LeaderboardVisibilitySnapshot()
            : this(Array.Empty<LeaderboardInstanceSpec>(), Array.Empty<LeaderboardInstanceSpec>(), Array.Empty<LeaderboardMetaMapping>())
        {
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<LeaderboardInstanceSpec> normalArchiveInstances, IEnumerable<LeaderboardMetaMapping> metaMappings)
            : this(normalArchiveInstances, Array.Empty<LeaderboardInstanceSpec>(), metaMappings)
        {
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<LeaderboardInstanceSpec> normalArchiveInstances,
            IEnumerable<LeaderboardInstanceSpec> changedInstances, IEnumerable<LeaderboardMetaMapping> metaMappings)
        {
            NormalArchiveInstances = LeaderboardStoreRecords.Copy(normalArchiveInstances, nameof(normalArchiveInstances));
            ChangedInstances = LeaderboardStoreRecords.Copy(changedInstances, nameof(changedInstances));
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<DBLeaderboardInstance> normalArchiveInstances, IEnumerable<DBMetaEntry> metaMappings)
            : this(
                LeaderboardStoreRecords.Convert(normalArchiveInstances, LeaderboardInstanceSpec.From, nameof(normalArchiveInstances)),
                LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<DBLeaderboardInstance> normalArchiveInstances,
            IEnumerable<DBLeaderboardInstance> changedInstances, IEnumerable<DBMetaEntry> metaMappings)
            : this(
                LeaderboardStoreRecords.Convert(normalArchiveInstances, LeaderboardInstanceSpec.From, nameof(normalArchiveInstances)),
                LeaderboardStoreRecords.Convert(changedInstances, LeaderboardInstanceSpec.From, nameof(changedInstances)),
                LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }
    }
}
