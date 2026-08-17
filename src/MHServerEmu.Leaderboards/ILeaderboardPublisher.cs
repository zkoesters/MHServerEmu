using MHServerEmu.Core.Network;

namespace MHServerEmu.Leaderboards
{
    public interface ILeaderboardPublisher
    {
        void Publish(ServiceMessage.LeaderboardStateChange change);
        void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes);
    }

    public sealed class ServerManagerLeaderboardPublisher : ILeaderboardPublisher
    {
        public void Publish(ServiceMessage.LeaderboardStateChange change)
        {
            ServerManager.Instance.SendMessageToService(GameServiceType.GameInstance, change);
        }

        public void Publish(IReadOnlyList<ServiceMessage.LeaderboardStateChange> changes)
        {
            ServerManager.Instance.SendMessageToService(GameServiceType.GameInstance, new ServiceMessage.LeaderboardStateChangeList(changes.ToList()));
        }
    }
}
