using MHServerEmu.Games.GameData.Prototypes;

namespace MHServerEmu.Leaderboards
{
    public interface ILeaderboardPrototypeCatalog
    {
        IReadOnlyList<LeaderboardPrototypeDefinition> GetPublicPrototypes();
        bool TryGetPrototype(long leaderboardId, out LeaderboardPrototype prototype);
    }

    public sealed record LeaderboardPrototypeDefinition(
        long LeaderboardId,
        string PrototypeName,
        bool IsEnabledByDefault,
        IReadOnlyList<long> SubLeaderboardIds);
}
