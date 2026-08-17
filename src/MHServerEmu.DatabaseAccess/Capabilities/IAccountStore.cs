using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess
{
    public interface IAccountStore
    {
        public bool TryQueryAccountByEmail(string email, out DBAccount account);
        public AccountStoreResult InsertAccount(DBAccount account);
        public AccountStoreResult ChangePlayerName(DBAccount account, string playerName);
        public AccountStoreResult ChangePassword(DBAccount account, byte[] passwordHash, byte[] salt);
        public AccountStoreResult ChangeUserLevel(DBAccount account, AccountUserLevel userLevel);
        public AccountStoreResult ChangeFlags(DBAccount account, AccountFlags flags);
        public AccountStoreResult ReconcileAccount(DBAccount account);
    }
}
