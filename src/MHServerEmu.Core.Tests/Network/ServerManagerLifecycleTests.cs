using MHServerEmu.Core.Network;

namespace MHServerEmu.Core.Tests.Network
{
    public class ServerManagerLifecycleTests
    {
        [Fact]
        public void RunServices_ServiceThrows_ReturnsFalseAndStopsStartedServices()
        {
            ServerManager manager = new();
            RunningService running = new();
            ThrowingService throwing = new();
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(throwing, GameServiceType.Leaderboard);

            Assert.False(manager.RunServices());

            Assert.Equal(GameServiceState.Shutdown, running.State);
            Assert.Equal(1, running.ShutdownCount);
        }

        private sealed class RunningService : IGameService
        {
            private int _shutdown;

            public GameServiceState State { get; private set; } = GameServiceState.Created;
            public int ShutdownCount { get; private set; }

            public void Run()
            {
                State = GameServiceState.Running;
                while (Volatile.Read(ref _shutdown) == 0)
                    Thread.Sleep(1);
                State = GameServiceState.Shutdown;
            }

            public void Shutdown()
            {
                ShutdownCount++;
                State = GameServiceState.ShuttingDown;
                Interlocked.Exchange(ref _shutdown, 1);
            }

            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }

        private sealed class ThrowingService : IGameService
        {
            public GameServiceState State { get; } = GameServiceState.Created;

            public void Run() => throw new InvalidOperationException("startup failure");
            public void Shutdown() { }
            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }
    }
}
