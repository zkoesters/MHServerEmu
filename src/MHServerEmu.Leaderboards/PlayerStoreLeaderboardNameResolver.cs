using MHServerEmu.DatabaseAccess;

namespace MHServerEmu.Leaderboards
{
    public sealed class PlayerStoreLeaderboardNameResolver : ILeaderboardPlayerNameResolver
    {
        private readonly IPlayerStore _players;
        private readonly Dictionary<ulong, string> _names = new();

        public PlayerStoreLeaderboardNameResolver(IPlayerStore players)
        {
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _players.GetPlayerNames(_names);
        }

        public string GetPlayerName(ulong participantId)
        {
            if (_names.TryGetValue(participantId, out string name))
                return name;

            if (_players.TryGetPlayerName(participantId, out name) == false)
                name = $"Player{participantId}";

            _names[participantId] = name;
            return name;
        }
    }
}
