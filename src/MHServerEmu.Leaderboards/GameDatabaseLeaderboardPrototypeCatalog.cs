using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Leaderboards
{
    public sealed class GameDatabaseLeaderboardPrototypeCatalog : ILeaderboardPrototypeCatalog
    {
        public IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes()
        {
            List<LeaderboardPrototypeDefinition> definitions = new();
            foreach (PrototypeId dataRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<LeaderboardPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                LeaderboardPrototype prototype = GameDatabase.GetPrototype<LeaderboardPrototype>(dataRef);
                if (prototype == null || prototype.DesignState != DesignWorkflowState.Live || prototype.Public == false)
                    continue;

                long leaderboardId = (long)GameDatabase.GetPrototypeGuid(dataRef);
                long[] subLeaderboardIds = prototype.MetaLeaderboardEntries?
                    .Select(entry => (long)GameDatabase.GetPrototypeGuid(entry.Leaderboard))
                    .ToArray() ?? Array.Empty<long>();
                definitions.Add(new LeaderboardPrototypeDefinition(leaderboardId, dataRef.GetNameFormatted(),
                    prototype.ResetFrequency == LeaderboardResetFrequency.NeverReset, subLeaderboardIds));
            }

            return definitions;
        }

        public bool TryGetPrototype(long leaderboardId, out LeaderboardPrototype prototype)
        {
            PrototypeId dataRef = GameDatabase.GetDataRefByPrototypeGuid((PrototypeGuid)leaderboardId);
            prototype = GameDatabase.GetPrototype<LeaderboardPrototype>(dataRef);
            return prototype != null;
        }
    }
}
