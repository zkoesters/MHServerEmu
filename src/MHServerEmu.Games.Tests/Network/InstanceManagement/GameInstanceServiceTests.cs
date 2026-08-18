using System.Collections.Concurrent;
using System.Reflection;
using MHServerEmu.Core.Network;
using MHServerEmu.Games.Network.InstanceManagement;

namespace MHServerEmu.Games.Tests.Network.InstanceManagement
{
    public class GameInstanceServiceTests
    {
        [Fact]
        public async Task Run_WaitsForShutdownAfterStartingWorkers()
        {
            GameInstanceService service = CreateService();
            Task run = Task.Run(service.Run);

            try
            {
                await WaitForStateAsync(service, GameServiceState.Running);
                Assert.False(run.IsCompleted);
            }
            finally
            {
                service.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public async Task Shutdown_BeforeRun_DoesNotStartWorkers()
        {
            GameInstanceService service = CreateService();
            service.Shutdown();
            Task run = Task.Run(service.Run);

            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(GameServiceState.Shutdown, service.State);
                Assert.Equal(0, GetThreadManager(service).ThreadCount);
            }
            finally
            {
                service.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task Shutdown_WaitsForWorkersToStopBeforeReleasingRun()
        {
            using ManualResetEventSlim initializationStarted = new();
            using ManualResetEventSlim continueInitialization = new();
            using ManualResetEventSlim shutdownRequested = new();
            int shutdownReturned = 0;
            int shutdownReturnedBeforeWorkerReleased = 0;
            GameInstanceService service = CreateCoordinatedService(() =>
            {
                initializationStarted.Set();
                continueInitialization.Wait();
                if (Volatile.Read(ref shutdownReturned) != 0)
                    Interlocked.Exchange(ref shutdownReturnedBeforeWorkerReleased, 1);
            }, beforeShutdownLock: shutdownRequested.Set);
            Task run = Task.Run(service.Run);

            try
            {
                await WaitForStateAsync(service, GameServiceState.Running);
                Assert.True(initializationStarted.Wait(TimeSpan.FromSeconds(5)));
                GameThread[] gameThreads = GetGameThreads(GetThreadManager(service));
                Assert.All(gameThreads, gameThread => Assert.Equal(GameThreadState.Starting, gameThread.State));

                Task shutdown = Task.Run(() =>
                {
                    service.Shutdown();
                    Volatile.Write(ref shutdownReturned, 1);
                });
                Assert.True(shutdownRequested.Wait(TimeSpan.FromSeconds(5)));

                continueInitialization.Set();
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                await run.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, Volatile.Read(ref shutdownReturnedBeforeWorkerReleased));
                Assert.All(gameThreads, gameThread => Assert.Equal(GameThreadState.Stopped, gameThread.State));
                Assert.Equal(0, GetThreadManager(service).ThreadCount);
            }
            finally
            {
                continueInitialization.Set();
                service.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task Run_ThreadStartFailureAfterAllocation_RollsBackAllWorkers()
        {
            using ManualResetEventSlim workerInitializationStarted = new();
            using ManualResetEventSlim continueWorkerInitialization = new();
            GameInstanceService service = CreateCoordinatedService(
                () =>
                {
                    workerInitializationStarted.Set();
                    continueWorkerInitialization.Wait();
                },
                failStart: threadId => threadId == 2);
            SetWorkerCount(service, 2);
            Task run = Task.Run(service.Run);

            try
            {
                Assert.True(workerInitializationStarted.Wait(TimeSpan.FromSeconds(5)));
                GameThread[] gameThreads = GetGameThreads(GetThreadManager(service));
                GameThread startedThread = Assert.Single(gameThreads.Where(gameThread => gameThread.Id == 1));

                continueWorkerInitialization.Set();
                await Assert.ThrowsAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));

                service.Shutdown();
                Assert.Equal(GameServiceState.Shutdown, service.State);
                Assert.Equal(GameThreadState.Stopped, startedThread.State);
                Assert.All(gameThreads, gameThread => Assert.Equal(GameThreadState.Stopped, gameThread.State));
                Assert.Equal(0, GetThreadManager(service).ThreadCount);
            }
            finally
            {
                continueWorkerInitialization.Set();
                try { await run; } catch (InvalidOperationException) { }
                service.Shutdown();
            }
        }

        [Fact]
        public async Task Shutdown_DuringRunInitialization_DoesNotPublishRunning()
        {
            using ManualResetEventSlim initializationBlocked = new();
            using ManualResetEventSlim continueInitialization = new();
            using ManualResetEventSlim shutdownRequested = new();
            ConcurrentQueue<GameServiceState> stateChanges = new();
            GameInstanceService service = CreateCoordinatedService(
                beforeInitializeComplete: () =>
                {
                    initializationBlocked.Set();
                    continueInitialization.Wait();
                },
                beforeShutdownLock: shutdownRequested.Set,
                stateChanged: stateChanges.Enqueue);
            Task run = Task.Run(service.Run);

            try
            {
                Assert.True(initializationBlocked.Wait(TimeSpan.FromSeconds(5)));
                GameThread[] gameThreads = GetGameThreads(GetThreadManager(service));
                Task shutdown = Task.Run(service.Shutdown);
                Assert.True(shutdownRequested.Wait(TimeSpan.FromSeconds(5)));

                Assert.Equal(GameServiceState.Created, service.State);
                continueInitialization.Set();
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                await run.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.DoesNotContain(GameServiceState.Running, stateChanges);
                Assert.Equal(GameServiceState.Shutdown, service.State);
                Assert.All(gameThreads, gameThread => Assert.Equal(GameThreadState.Stopped, gameThread.State));
                Assert.Equal(0, GetThreadManager(service).ThreadCount);
            }
            finally
            {
                continueInitialization.Set();
                service.Shutdown();
                await run.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private static async Task WaitForStateAsync(GameInstanceService service, GameServiceState expectedState)
        {
            await Task.Run(() => SpinWait.SpinUntil(() => service.State == expectedState, TimeSpan.FromSeconds(5)));
            Assert.Equal(expectedState, service.State);
        }

        private static GameThreadManager GetThreadManager(GameInstanceService service)
        {
            PropertyInfo gameThreadManager = typeof(GameInstanceService).GetProperty("GameThreadManager", BindingFlags.Instance | BindingFlags.NonPublic);
            return Assert.IsType<GameThreadManager>(gameThreadManager.GetValue(service));
        }

        private static GameThread[] GetGameThreads(GameThreadManager gameThreadManager)
        {
            FieldInfo gameThreads = typeof(GameThreadManager).GetField("_gameThreads", BindingFlags.Instance | BindingFlags.NonPublic);
            Dictionary<uint, GameThread> threadDictionary = Assert.IsType<Dictionary<uint, GameThread>>(gameThreads.GetValue(gameThreadManager));
            return threadDictionary.Values.ToArray();
        }

        private static GameInstanceService CreateService(Action initializeThreadLocalStorage = null)
        {
            ConstructorInfo constructor = typeof(GameInstanceService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, new[] { typeof(Action) });
            return Assert.IsType<GameInstanceService>(constructor.Invoke(new Action[] { initializeThreadLocalStorage ?? (() => { }) }));
        }

        private static GameInstanceService CreateCoordinatedService(Action initializeThreadLocalStorage = null, Action beforeInitializeComplete = null,
            Func<uint, bool> failStart = null, Action beforeShutdownLock = null, Action<GameServiceState> stateChanged = null)
        {
            ConstructorInfo constructor = typeof(GameInstanceService).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                new[] { typeof(Action), typeof(Action), typeof(Func<uint, bool>), typeof(Action), typeof(Action<GameServiceState>) });
            return Assert.IsType<GameInstanceService>(constructor.Invoke(new object[] { initializeThreadLocalStorage ?? (() => { }), beforeInitializeComplete, failStart, beforeShutdownLock, stateChanged }));
        }

        private static void SetWorkerCount(GameInstanceService service, int workerCount)
        {
            PropertyInfo numWorkerThreads = typeof(GameInstanceConfig).GetProperty("NumWorkerThreads", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            numWorkerThreads.SetValue(service.Config, workerCount);
        }
    }
}
