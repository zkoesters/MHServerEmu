namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardVisibilitySnapshot
    {
        public IReadOnlyList<LeaderboardInstanceSpec> NormalArchiveInstances { get; }
        public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; }

        public LeaderboardVisibilitySnapshot()
            : this(Array.Empty<LeaderboardInstanceSpec>(), Array.Empty<LeaderboardMetaMapping>())
        {
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<LeaderboardInstanceSpec> normalArchiveInstances, IEnumerable<LeaderboardMetaMapping> metaMappings)
        {
            NormalArchiveInstances = LeaderboardStoreRecords.Copy(normalArchiveInstances, nameof(normalArchiveInstances));
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
        }

        public LeaderboardVisibilitySnapshot(IEnumerable<DBLeaderboardInstance> normalArchiveInstances, IEnumerable<DBMetaEntry> metaMappings)
            : this(
                LeaderboardStoreRecords.Convert(normalArchiveInstances, LeaderboardInstanceSpec.From, nameof(normalArchiveInstances)),
                LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }
    }
}
