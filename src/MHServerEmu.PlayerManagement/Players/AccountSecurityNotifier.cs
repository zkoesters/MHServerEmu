using MHServerEmu.Core.Network;

namespace MHServerEmu.PlayerManagement.Players
{
    internal interface IAccountSecurityNotifier
    {
        void Notify(ulong accountId, AccountSecurityChangeType changeType);
    }

    internal sealed class ServerAccountSecurityNotifier : IAccountSecurityNotifier
    {
        public void Notify(ulong accountId, AccountSecurityChangeType changeType)
        {
            ServiceMessage.AccountSecurityChanged message = new(accountId, changeType);
            ServerManager.Instance.SendMessageToService(GameServiceType.PlayerManager, message);
        }
    }
}
