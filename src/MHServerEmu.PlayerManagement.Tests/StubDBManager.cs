using MHServerEmu.DatabaseAccess;
using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.PlayerManagement.Tests
{
    public sealed class StubDBManager : IDBManager, IAccountStore, IPlayerStore, IGuildStore
    {
        public Dictionary<string, DBAccount> Accounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DBGuild> GuildsToLoad { get; } = new();

        public bool VerifyAccounts { get; set; } = true;
        public bool InitializeResult { get; set; } = true;
        public bool InsertAccountResult { get; set; } = true;
        public bool UpdateAccountResult { get; set; } = true;
        public bool LoadPlayerDataResult { get; set; } = true;
        public bool SavePlayerDataResult { get; set; } = true;
        public bool ThrowOnUpdateAccount { get; set; }
        public int UpdateAccountCallCount { get; private set; }
        public int TryGetPlayerNameCallCount { get; private set; }
        public int TryGetPlayerDbIdByNameCallCount { get; private set; }
        public int LoadPlayerDataCallCount { get; private set; }
        public int SavePlayerDataCallCount { get; private set; }
        public int LoadGuildsCallCount { get; private set; }
        public int SaveGuildCallCount { get; private set; }
        public int DeleteGuildCallCount { get; private set; }
        public int SaveGuildMemberCallCount { get; private set; }
        public int DeleteGuildMemberCallCount { get; private set; }

        public bool Initialize()
        {
            return InitializeResult;
        }

        public bool TryQueryAccountByEmail(string email, out DBAccount account)
        {
            return Accounts.TryGetValue(email, out account);
        }

        public bool TryGetPlayerDbIdByName(string playerName, out ulong playerDbId, out string playerNameOut)
        {
            TryGetPlayerDbIdByNameCallCount++;

            foreach (DBAccount account in Accounts.Values)
            {
                if (string.Equals(account.PlayerName, playerName, StringComparison.OrdinalIgnoreCase))
                {
                    playerDbId = (ulong)account.Id;
                    playerNameOut = account.PlayerName;
                    return true;
                }
            }

            playerDbId = 0;
            playerNameOut = null;
            return false;
        }

        public bool TryGetPlayerName(ulong playerDbId, out string playerName)
        {
            TryGetPlayerNameCallCount++;

            foreach (DBAccount account in Accounts.Values)
            {
                if ((ulong)account.Id == playerDbId)
                {
                    playerName = account.PlayerName;
                    return true;
                }
            }

            playerName = null;
            return false;
        }

        public bool GetPlayerNames(Dictionary<ulong, string> playerNames)
        {
            playerNames.Clear();
            foreach (DBAccount account in Accounts.Values)
                playerNames[(ulong)account.Id] = account.PlayerName;

            return playerNames.Count > 0;
        }

        public bool TryGetLastLogoutTime(ulong playerDbId, out long lastLogoutTime)
        {
            lastLogoutTime = 0;
            return false;
        }

        public bool InsertAccount(DBAccount account)
        {
            if (InsertAccountResult == false)
                return false;

            Accounts[account.Email] = account;
            return true;
        }

        public bool UpdateAccount(DBAccount account)
        {
            UpdateAccountCallCount++;

            if (ThrowOnUpdateAccount)
                throw new InvalidOperationException("Configured account update failure.");

            return UpdateAccountResult;
        }

        public bool LoadPlayerData(DBAccount account)
        {
            LoadPlayerDataCallCount++;
            return LoadPlayerDataResult;
        }

        public bool SavePlayerData(DBAccount account)
        {
            SavePlayerDataCallCount++;
            return SavePlayerDataResult;
        }

        public bool LoadGuilds(List<DBGuild> guilds)
        {
            LoadGuildsCallCount++;
            guilds.AddRange(GuildsToLoad);
            return true;
        }

        public bool SaveGuild(DBGuild guild)
        {
            SaveGuildCallCount++;
            return true;
        }

        public bool DeleteGuild(DBGuild guild)
        {
            DeleteGuildCallCount++;
            return true;
        }

        public bool SaveGuildMember(DBGuildMember guildMember)
        {
            SaveGuildMemberCallCount++;
            return true;
        }

        public bool DeleteGuildMember(DBGuildMember guildMember)
        {
            DeleteGuildMemberCallCount++;
            return true;
        }
    }
}
