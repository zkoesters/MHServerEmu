using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess
{
    public interface IPlayerStore
    {
        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut);
        public bool TryGetPlayerName(ulong playerDbId, out string playerName);
        public bool GetPlayerNames(Dictionary<ulong, string> playerNames);
        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime);
        public PlayerStoreResult LoadPlayerData(DBAccount account);
        public PlayerStoreResult SavePlayerData(DBAccount account);
    }
}
