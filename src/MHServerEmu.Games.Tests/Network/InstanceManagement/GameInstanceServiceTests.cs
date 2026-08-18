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
            GameInstanceService service = CreateService(() =>
            {
                initializationStarted.Set();
                continueInitialization.Wait();
            });
            Task run = Task.Run(service.Run);

            try
            {
                await WaitForStateAsync(service, GameServiceState.Running);
                Assert.True(initializationStarted.Wait(TimeSpan.FromSeconds(5)));
                GameThread[] gameThreads = GetGameThreads(GetThreadManager(service));
                Assert.All(gameThreads, gameThread => Assert.Equal(GameThreadState.Starting, gameThread.State));

                Task shutdown = Task.Run(service.Shutdown);
                Assert.False(shutdown.Wait(TimeSpan.FromMilliseconds(100)));

                continueInitialization.Set();
                await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
                await run.WaitAsync(TimeSpan.FromSeconds(5));

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
    }
}
