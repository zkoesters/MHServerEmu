using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.Tests
{
    public class ServerAppLifecycleTests
    {
        [Fact]
        public async Task Shutdown_WhileServiceIsStarting_StopsStartupAndDisposesOnce()
        {
            int disposeCount = 0;
            StartingService starting = new();
            PersistenceRuntime runtime = CreateRuntime(() => Interlocked.Increment(ref disposeCount));
            ServerStartupDependencies dependencies = new(
                (_, _) => Task.FromResult(runtime),
                () => true,
                (manager, _, _) => manager.RegisterGameService(starting, GameServiceType.GameInstance),
                () => Task.FromResult<string>(null));
            ServerApp app = new(dependencies, new ServerManager());

            Task run = Task.Run(app.RunAsync);
            await starting.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            app.Shutdown();
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, starting.ShutdownCount);
            Assert.Equal(1, disposeCount);
        }

        [Fact]
        public async Task FatalCallback_WhileConsoleReadPending_StopsAndDisposesOnce()
        {
            TaskCompletionSource<bool> persistenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> consoleRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<PersistenceFatalFailure> fatalCallback = null;
            int disposeCount = 0;
            PersistenceRuntime runtime = CreateRuntime(() => Interlocked.Increment(ref disposeCount));
            ServerStartupDependencies dependencies = new(
                (callback, _) =>
                {
                    fatalCallback = callback;
                    persistenceStarted.SetResult(true);
                    return Task.FromResult(runtime);
                },
                () => true,
                (_, _, _) => { },
                () => consoleRead.Task);
            ServerApp app = new(dependencies, new ServerManager());

            Task run = app.RunAsync();
            await persistenceStarted.Task;
            fatalCallback(new PersistenceFatalFailure("WriterLockLost", "WriterMonitor"));
            await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, disposeCount);
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

        private static PersistenceRuntime CreateRuntime(Action dispose)
        {
            JsonDBManager manager = JsonDBManager.Instance;
            return new PersistenceRuntime(new PersistenceServices(manager, manager, manager,
                new SQLiteLeaderboardDBManager("unused.db"), PersistenceCapabilities.Json), () =>
            {
                dispose();
                return ValueTask.CompletedTask;
            });
        }
    }
}
