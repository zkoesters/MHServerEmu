namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardSnapshot
    {
        public IReadOnlyList<LeaderboardDefinitionSpec> Definitions { get; }
        public IReadOnlyList<LeaderboardInstanceSpec> NonterminalInstances { get; }
        public IReadOnlyList<LeaderboardInstanceSpec> NormalArchiveInstances { get; }
        public IReadOnlyList<LeaderboardMetaMapping> MetaMappings { get; }

        public LeaderboardSnapshot()
            : this(Array.Empty<LeaderboardDefinitionSpec>(), Array.Empty<LeaderboardInstanceSpec>(), Array.Empty<LeaderboardInstanceSpec>(), Array.Empty<LeaderboardMetaMapping>())
        {
        }

        public LeaderboardSnapshot(IEnumerable<LeaderboardDefinitionSpec> definitions, IEnumerable<LeaderboardInstanceSpec> nonterminalInstances, IEnumerable<LeaderboardInstanceSpec> normalArchiveInstances, IEnumerable<LeaderboardMetaMapping> metaMappings)
        {
            Definitions = LeaderboardStoreRecords.Copy(definitions, nameof(definitions));
            NonterminalInstances = LeaderboardStoreRecords.Copy(nonterminalInstances, nameof(nonterminalInstances));
            NormalArchiveInstances = LeaderboardStoreRecords.Copy(normalArchiveInstances, nameof(normalArchiveInstances));
            MetaMappings = LeaderboardStoreRecords.Copy(metaMappings, nameof(metaMappings));
        }

        public LeaderboardSnapshot(IEnumerable<DBLeaderboard> definitions, IEnumerable<DBLeaderboardInstance> nonterminalInstances, IEnumerable<DBLeaderboardInstance> normalArchiveInstances, IEnumerable<DBMetaEntry> metaMappings)
            : this(
                LeaderboardStoreRecords.Convert(definitions, LeaderboardDefinitionSpec.From, nameof(definitions)),
                LeaderboardStoreRecords.Convert(nonterminalInstances, LeaderboardInstanceSpec.From, nameof(nonterminalInstances)),
                LeaderboardStoreRecords.Convert(normalArchiveInstances, LeaderboardInstanceSpec.From, nameof(normalArchiveInstances)),
                LeaderboardStoreRecords.Convert(metaMappings, LeaderboardMetaMapping.From, nameof(metaMappings)))
        {
        }
    }
}
