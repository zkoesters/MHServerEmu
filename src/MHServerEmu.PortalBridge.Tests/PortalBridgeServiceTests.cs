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

            try
            {
                thread.Start();
                Assert.True(SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2)));
                Assert.False(service.IsAvailable);
            }
            finally
            {
                service.Shutdown();
                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
            }

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

            try
            {
                thread.Start();
                Assert.True(SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2)));
                Assert.False(service.IsAvailable);
            }
            finally
            {
                service.Shutdown();
                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
                File.Delete(secretFile);
            }

            Assert.Equal(GameServiceState.Shutdown, service.State);
        }

        [Fact]
        public void Shutdown_BeforeRun_IsRememberedWhenRunTransitionsThroughStarting()
        {
            PortalBridgeService service = CreateService(CreateConfig("/missing/portal-bridge-secret", GetFreePort()));
            Thread thread = new(service.Run);

            service.Shutdown();
            try
            {
                thread.Start();
                Assert.True(SpinWait.SpinUntil(() => service.State == GameServiceState.Starting ||
                    service.State == GameServiceState.Running || service.State == GameServiceState.Shutdown,
                    TimeSpan.FromSeconds(2)));
                Assert.False(service.IsAvailable);
            }
            finally
            {
                service.Shutdown();
                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
            }

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

            try
            {
                LogManager.Enabled = true;
                targetAttached = LogManager.AttachTarget(target);
                Assert.True(targetAttached);

                thread.Start();
                LogMessage message = target.Message.Task.Wait(TimeSpan.FromSeconds(2))
                    ? target.Message.Task.Result
                    : throw new TimeoutException("PortalBridge startup exception was not logged.");

                Assert.Contains("[Exception]", message.Message, StringComparison.Ordinal);
                Assert.Contains(nameof(HttpListenerException), message.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, message.Message, StringComparison.Ordinal);
            }
            finally
            {
                service.Shutdown();
                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
                if (targetAttached)
                    Assert.True(LogManager.DetachTarget(target));
                LogManager.Enabled = loggingEnabled;
                File.Delete(secretFile);
            }
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
