using MHServerEmu.Core.Network;
using MHServerEmu.DatabaseAccess.Json;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.SQLite;

namespace MHServerEmu.Tests
{
    public class ServerAppLifecycleTests
    {
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
