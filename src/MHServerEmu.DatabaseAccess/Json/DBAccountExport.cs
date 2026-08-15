using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Json
{
    internal class DBAccountExport
    {
        public long Id { get; init; }
        public string Email { get; init; }
        public string PlayerName { get; init; }
        public AccountUserLevel UserLevel { get; init; }
        public AccountFlags Flags { get; init; }
        public DBPlayer Player { get; init; }
        public DBEntityCollection Avatars { get; init; }
        public DBEntityCollection TeamUps { get; init; }
        public DBEntityCollection Items { get; init; }
        public DBEntityCollection ControlledEntities { get; init; }

        public static DBAccountExport FromAccount(DBAccount account)
        {
            return new()
            {
                Id = account.Id,
                Email = account.Email,
                PlayerName = account.PlayerName,
                UserLevel = account.UserLevel,
                Flags = account.Flags,
                Player = account.Player,
                Avatars = account.Avatars,
                TeamUps = account.TeamUps,
                Items = account.Items,
                ControlledEntities = account.ControlledEntities
            };
        }
    }
}
