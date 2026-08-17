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

        [Fact]
        public async Task RunServices_ServiceStopsDuringStartup_ReturnsFalseWithoutWaitingIndefinitely()
        {
            ServerManager manager = new();
            manager.RegisterGameService(new StoppedService(), GameServiceType.GameInstance);

            Task<bool> run = Task.Run(manager.RunServices);

            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public async Task ShutdownServices_WhileServiceIsStarting_RequestsServiceShutdownAndUnblocksStartup()
        {
            ServerManager manager = new();
            StartingService starting = new();
            manager.RegisterGameService(starting, GameServiceType.GameInstance);

            Task<bool> run = Task.Run(manager.RunServices);
            await starting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manager.ShutdownServices();

            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, starting.ShutdownCount);
        }

        [Fact]
        public async Task RunServices_EarlierServiceFaultsWhileLaterServiceIsStarting_ReturnsFalseAndStopsLaterService()
        {
            ServerManager manager = new();
            PostRunningThrowingService throwing = new();
            StartingService starting = new();
            manager.RegisterGameService(throwing, GameServiceType.GameInstance);
            manager.RegisterGameService(starting, GameServiceType.Leaderboard);

            Task<bool> run = Task.Run(manager.RunServices);
            await starting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            throwing.Throw();

            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, starting.ShutdownCount);
        }

        [Fact]
        public async Task RunServices_ServiceThrowsAfterRunning_ReportsFaultAndShutdownDoesNotHang()
        {
            ServerManager manager = new();
            RunningService running = new();
            PostRunningThrowingService throwing = new();
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(throwing, GameServiceType.Leaderboard);

            Assert.True(manager.RunServices());
            throwing.Throw();

            await manager.WaitForFaultAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Task shutdown = Task.Run(manager.ShutdownServices);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

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

        private sealed class StoppedService : IGameService
        {
            public GameServiceState State { get; private set; } = GameServiceState.Created;

            public void Run() => State = GameServiceState.Shutdown;
            public void Shutdown() => State = GameServiceState.Shutdown;
            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }

        private sealed class StartingService : IGameService
        {
            private readonly ManualResetEventSlim _shutdown = new();

            public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public GameServiceState State { get; private set; } = GameServiceState.Created;
            public int ShutdownCount { get; private set; }

            public void Run()
            {
                Started.TrySetResult(true);
                _shutdown.Wait();
                State = GameServiceState.Shutdown;
            }

            public void Shutdown()
            {
                ShutdownCount++;
                State = GameServiceState.ShuttingDown;
                _shutdown.Set();
            }

            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }

        private sealed class PostRunningThrowingService : IGameService
        {
            private readonly ManualResetEventSlim _throw = new();

            public GameServiceState State { get; private set; } = GameServiceState.Created;

            public void Run()
            {
                State = GameServiceState.Running;
                _throw.Wait();
                throw new InvalidOperationException("runtime failure");
            }

            public void Throw() => _throw.Set();
            public void Shutdown() => State = GameServiceState.Shutdown;
            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }
    }
}
