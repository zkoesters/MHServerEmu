using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess
{
    public interface IAccountStore
    {
        public bool TryQueryAccountByEmail(string email, out DBAccount account);
        public bool InsertAccount(DBAccount account);
        public bool UpdateAccount(DBAccount account);
    }
}
