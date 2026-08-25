using MHServerEmu.Core.Network;

namespace MHServerEmu.Core.Tests.Network
{
    public class ServerManagerTests
    {
        [Fact]
        public void RunServices_SuccessfulStartup_RunsServices()
        {
            ServerManager manager = new();
            TestService service = TestService.Running();
            manager.RegisterGameService(service, GameServiceType.GameInstance);

            manager.RunServices();

            Assert.Equal(1, service.RunCalls);
            manager.ShutdownServices();
        }

        [Fact]
        public void RunServices_ServiceReturnsBeforeRunning_ShutsDownEarlierServicesAndSkipsLaterServices()
        {
            ServerManager manager = new();
            TestService running = TestService.Running();
            TestService returned = TestService.Returning();
            TestService later = TestService.Running();
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(returned, GameServiceType.Leaderboard);
            manager.RegisterGameService(later, GameServiceType.PlayerManager);

            Assert.Throws<InvalidOperationException>(() => manager.RunServices());

            Assert.Equal(1, running.ShutdownCalls);
            Assert.Equal(0, later.RunCalls);
            Assert.Equal(0, later.ShutdownCalls);
            Assert.Equal(ServerManagerState.Shutdown, manager.State);
        }

        [Fact]
        public void RunServices_StartupException_PreservesExceptionAndCleansUp()
        {
            ServerManager manager = new();
            TestService running = TestService.Running();
            InvalidOperationException expected = new("startup failure");
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(TestService.Throwing(expected), GameServiceType.Leaderboard);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => manager.RunServices());

            Assert.Same(expected, actual);
            Assert.Equal(1, running.ShutdownCalls);
            Assert.Equal(ServerManagerState.Shutdown, manager.State);
        }

        [Fact]
        public void RunServices_StartupException_PreservesExceptionWhenCleanupFails()
        {
            ServerManager manager = new();
            InvalidOperationException expected = new("startup failure");
            TestService running = TestService.RunningWithShutdownException(new InvalidOperationException("cleanup failure"));
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(TestService.Throwing(expected), GameServiceType.Leaderboard);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => manager.RunServices());

            Assert.Same(expected, actual);
            Assert.Equal(1, running.ShutdownCalls);
            Assert.Equal(ServerManagerState.Shutdown, manager.State);
        }

        [Fact]
        public void RunServices_StartupException_ContinuesCleanupWhenServiceShutdownFails()
        {
            ServerManager manager = new();
            InvalidOperationException expected = new("startup failure");
            TestService first = TestService.RunningWithShutdownException(new InvalidOperationException("first cleanup failure"));
            TestService later = TestService.RunningWithShutdownException(new InvalidOperationException("cleanup failure"));
            manager.RegisterGameService(first, GameServiceType.GameInstance);
            manager.RegisterGameService(later, GameServiceType.Leaderboard);
            manager.RegisterGameService(TestService.Throwing(expected), GameServiceType.PlayerManager);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => manager.RunServices());

            Assert.Same(expected, actual);
            Assert.Equal(ServerManagerState.Shutdown, manager.State);
            Assert.Equal(1, later.ShutdownCalls);
            Assert.Equal(1, first.ShutdownCalls);
        }

        [Fact]
        public void ShutdownServices_ServiceShutdownException_PropagatesException()
        {
            ServerManager manager = new();
            InvalidOperationException expected = new("shutdown failure");
            TestService service = TestService.RunningWithShutdownException(expected);
            manager.RegisterGameService(service, GameServiceType.GameInstance);
            manager.RunServices();

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => manager.ShutdownServices());

            Assert.Same(expected, actual);
            Assert.Equal(1, service.ShutdownCalls);
        }

        [Fact]
        public void ShutdownServices_AfterStartupCleanup_IsIdempotent()
        {
            ServerManager manager = new();
            TestService running = TestService.Running();
            manager.RegisterGameService(running, GameServiceType.GameInstance);
            manager.RegisterGameService(TestService.Returning(), GameServiceType.Leaderboard);

            Assert.Throws<InvalidOperationException>(() => manager.RunServices());
            manager.ShutdownServices();
            manager.ShutdownServices();

            Assert.Equal(1, running.ShutdownCalls);
        }

        [Fact]
        public void RunServices_StartupFailure_DoesNotShutdownCreatedServiceWithoutThread()
        {
            ServerManager manager = new();
            TestService created = TestService.CreatedWithStateReadFailure();
            manager.RegisterGameService(TestService.Throwing(new InvalidOperationException("startup failure")), GameServiceType.GameInstance);
            manager.RegisterGameService(created, GameServiceType.Leaderboard);

            Assert.Throws<InvalidOperationException>(() => manager.RunServices());

            Assert.Equal(0, created.ShutdownCalls);
        }

        [Fact]
        public void ShutdownServices_CreatedServiceWithoutThread_DoesNotReadItsState()
        {
            ServerManager manager = new();
            manager.RegisterGameService(TestService.Running(), GameServiceType.GameInstance);
            manager.RunServices();
            TestService created = TestService.CreatedWithStateReadFailure();
            manager.RegisterGameService(created, GameServiceType.Leaderboard);

            manager.ShutdownServices();

            Assert.Equal(0, created.ShutdownCalls);
        }

        private sealed class TestService : IGameService
        {
            private readonly Action<TestService> _run;
            private readonly Action<TestService> _shutdown;
            private readonly bool _throwWhenStateRead;
            private GameServiceState _state = GameServiceState.Created;

            private TestService(Action<TestService> run, Action<TestService> shutdown = null, bool throwWhenStateRead = false)
            {
                _run = run;
                _shutdown = shutdown ?? (service => service.State = GameServiceState.Shutdown);
                _throwWhenStateRead = throwWhenStateRead;
            }

            public GameServiceState State
            {
                get
                {
                    if (_throwWhenStateRead)
                        throw new InvalidOperationException("Created service state must not be read without a thread.");
                    return _state;
                }
                private set => _state = value;
            }
            public int RunCalls { get; private set; }
            public int ShutdownCalls { get; private set; }

            public static TestService Running() => new(service => service.State = GameServiceState.Running);
            public static TestService RunningWithShutdownException(Exception exception) => new(service => service.State = GameServiceState.Running, _ => throw exception);
            public static TestService Returning() => new(service => service.State = GameServiceState.Starting);
            public static TestService Throwing(Exception exception) => new(_ => throw exception);
            public static TestService CreatedWithStateReadFailure() => new(_ => { }, throwWhenStateRead: true);

            public void Run() { RunCalls++; _run(this); }
            public void Shutdown() { ShutdownCalls++; _shutdown(this); }
            public void ReceiveServiceMessage<T>(in T message) where T : struct, IGameServiceMessage { }
            public void GetStatus(Dictionary<string, long> statusDict) { }
        }
    }
}
