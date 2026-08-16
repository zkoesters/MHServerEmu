using MHServerEmu.DatabaseAccess.Models;

namespace MHServerEmu.DatabaseAccess.Tests.Conformance
{
    internal static class AccountStoreConformanceCases
    {
        internal static void AssertOutcomeUncertain(IAccountStore store, DBAccount account)
        {
            account.PersistenceState = PersistenceState.OutcomeUncertain;

            Assert.Equal(AccountStoreResult.OutcomeUncertain, store.ChangePlayerName(account, "PlayerTwo"));
            Assert.Equal(AccountStoreResult.OutcomeUncertain, store.ChangePassword(account, Enumerable.Repeat((byte)0xA1, 64).ToArray(), Enumerable.Repeat((byte)0xB2, 64).ToArray()));
            Assert.Equal(AccountStoreResult.OutcomeUncertain, store.ChangeUserLevel(account, AccountUserLevel.Admin));
            Assert.Equal(AccountStoreResult.OutcomeUncertain, store.ChangeFlags(account, AccountFlags.IsBanned));
        }
    }
}
