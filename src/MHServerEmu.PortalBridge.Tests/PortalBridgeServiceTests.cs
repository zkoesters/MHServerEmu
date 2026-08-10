using System.Net;
using System.Net.Sockets;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Network;

namespace MHServerEmu.PortalBridge.Tests
{
    public class PortalBridgeServiceTests
    {
        [Fact]
        public void Run_InvalidSecret_ReachesRunningUnavailableAndShutsDown()
        {
            PortalBridgeConfig config = CreateConfig("/missing/portal-bridge-secret", GetFreePort());
            PortalBridgeService service = CreateService(config);
            Thread thread = new(service.Run);
            bool threadStarted = false;
            bool threadJoined = false;
            bool reachedRunning = false;
            bool available = true;

            try
            {
                thread.Start();
                threadStarted = true;
                reachedRunning = SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2));
                available = service.IsAvailable;
            }
            finally
            {
                service.Shutdown();
                if (threadStarted)
                    threadJoined = thread.Join(TimeSpan.FromSeconds(2));
            }

            Assert.True(reachedRunning);
            Assert.False(available);
            Assert.True(threadJoined);
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public void Run_OccupiedPort_ReachesRunningUnavailableAndShutsDown()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            string secretFile = Path.GetTempFileName();
            File.WriteAllText(secretFile, Convert.ToBase64String(new byte[32]));
            PortalBridgeService service = CreateService(CreateConfig(secretFile, ((IPEndPoint)listener.LocalEndpoint).Port));
            Thread thread = new(service.Run);
            bool threadStarted = false;
            bool threadJoined = false;
            bool reachedRunning = false;
            bool available = true;
            bool secretDeleted = false;

            try
            {
                thread.Start();
                threadStarted = true;
                reachedRunning = SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2));
                available = service.IsAvailable;
            }
            finally
            {
                try
                {
                    service.Shutdown();
                    if (threadStarted)
                        threadJoined = thread.Join(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    File.Delete(secretFile);
                    secretDeleted = File.Exists(secretFile) == false;
                }
            }

            Assert.True(reachedRunning);
            Assert.False(available);
            Assert.True(threadJoined);
            Assert.True(secretDeleted);
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public void Shutdown_DuringStarting_PreventsListenerActivity()
        {
            string secretFile = Path.GetTempFileName();
            File.WriteAllText(secretFile, Convert.ToBase64String(new byte[32]));
            using ManualResetEventSlim starting = new(false);
            using ManualResetEventSlim releaseStartup = new(false);
            PortalBridgeService service = CreateService(CreateConfig(secretFile, GetFreePort()), () =>
            {
                starting.Set();
                releaseStartup.Wait();
            });
            Thread thread = new(service.Run);
            bool threadStarted = false;
            bool threadJoined = false;
            bool secretDeleted = false;
            bool startupBlocked = false;
            GameServiceState stateWhileBlocked = GameServiceState.Created;

            try
            {
                thread.Start();
                threadStarted = true;
                startupBlocked = starting.Wait(TimeSpan.FromSeconds(2));
                stateWhileBlocked = service.State;

                service.Shutdown();
                releaseStartup.Set();
                threadJoined = thread.Join(TimeSpan.FromSeconds(2));
            }
            finally
            {
                try
                {
                    service.Shutdown();
                    releaseStartup.Set();
                    if (threadStarted && threadJoined == false)
                        threadJoined = thread.Join(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    File.Delete(secretFile);
                    secretDeleted = File.Exists(secretFile) == false;
                }
            }

            Assert.True(threadJoined);
            Assert.True(secretDeleted);
            Assert.True(startupBlocked);
            Assert.Equal(GameServiceState.Starting, stateWhileBlocked);
            Assert.False(service.IsAvailable);
            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public void Run_ListenerStartupFailure_LogsExceptionWithoutSecretContents()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            string secretFile = Path.GetTempFileName();
            string secret = Convert.ToBase64String(new byte[32]);
            File.WriteAllText(secretFile, secret);
            PortalBridgeService service = CreateService(CreateConfig(secretFile, ((IPEndPoint)listener.LocalEndpoint).Port));
            Thread thread = new(service.Run);
            CapturingLogTarget target = new();
            bool loggingEnabled = LogManager.Enabled;
            bool targetAttached = false;
            bool targetDetached = false;
            bool threadStarted = false;
            bool threadJoined = false;
            bool secretDeleted = false;
            bool messageReceived = false;
            LogMessage message = default;

            try
            {
                LogManager.Enabled = true;
                targetAttached = LogManager.AttachTarget(target);
                thread.Start();
                threadStarted = true;
                messageReceived = target.Message.Task.Wait(TimeSpan.FromSeconds(2));
                if (messageReceived)
                    message = target.Message.Task.Result;
            }
            finally
            {
                try
                {
                    service.Shutdown();
                    if (threadStarted)
                        threadJoined = thread.Join(TimeSpan.FromSeconds(2));
                }
                finally
                {
                    try
                    {
                        if (targetAttached)
                            targetDetached = LogManager.DetachTarget(target);
                    }
                    finally
                    {
                        LogManager.Enabled = loggingEnabled;
                        File.Delete(secretFile);
                        secretDeleted = File.Exists(secretFile) == false;
                    }
                }
            }

            Assert.True(targetAttached);
            Assert.True(messageReceived);
            Assert.True(threadJoined);
            Assert.True(targetDetached);
            Assert.True(secretDeleted);
            Assert.Contains("[Exception]", message.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(HttpListenerException), message.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, message.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void GameServiceType_PortalBridgeAppendsWithoutRenumberingExistingServices()
        {
            Assert.Equal(5, (int)GameServiceType.WebFrontend);
            Assert.Equal(6, (int)GameServiceType.PortalBridge);
        }

        private static PortalBridgeService CreateService(PortalBridgeConfig config)
        {
            return new PortalBridgeService(config, new PortalBridgeMetadata("1.0.2", new string('a', 40), "1.52.0.1700"),
                () => GameServiceState.Running);
        }

        private static PortalBridgeService CreateService(PortalBridgeConfig config, Action startingAction)
        {
            return new PortalBridgeService(config, new PortalBridgeMetadata("1.0.2", new string('a', 40), "1.52.0.1700"),
                () => GameServiceState.Running, null, null, startingAction);
        }

        private static PortalBridgeConfig CreateConfig(string secretFile, int port)
        {
            return new PortalBridgeConfig
            {
                Enabled = true,
                Address = "127.0.0.1",
                Port = port,
                KeyId = "portal-primary",
                SecretFile = secretFile,
                ServerInstanceId = "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a",
            };
        }

        private static int GetFreePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private sealed class CapturingLogTarget : LogTarget
        {
            public TaskCompletionSource<LogMessage> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CapturingLogTarget() : base(new LogTargetSettings
            {
                MinimumLevel = LoggingLevel.Error,
                MaximumLevel = LoggingLevel.Error,
                Channels = LogChannels.All,
            })
            {
            }

            public override void ProcessLogMessage(in LogMessage message)
            {
                if (message.Logger == nameof(PortalBridgeService))
                    Message.TrySetResult(message);
            }
        }
    }
}
