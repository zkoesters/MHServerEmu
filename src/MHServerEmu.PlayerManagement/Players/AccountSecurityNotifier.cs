using MHServerEmu.Core.Network;

namespace MHServerEmu.PlayerManagement.Players
{
    public interface IAccountSecurityNotifier
    {
        void Notify(ulong accountId, AccountSecurityChangeType changeType);
    }

    public sealed class ServerAccountSecurityNotifier : IAccountSecurityNotifier
    {
        public void Notify(ulong accountId, AccountSecurityChangeType changeType)
        {
            ServiceMessage.AccountSecurityChanged message = new(accountId, changeType);
            ServerManager.Instance.SendMessageToService(GameServiceType.PlayerManager, message);
        }
    }
}
