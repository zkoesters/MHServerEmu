using System.Net;
using System.Net.Sockets;
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

            thread.Start();
            Assert.True(SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2)));
            Assert.False(service.IsAvailable);

            service.Shutdown();

            Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
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

                service.Shutdown();

                Assert.True(thread.Join(TimeSpan.FromSeconds(2)));
                Assert.Equal(GameServiceState.Shutdown, service.State);
            }
            finally
            {
                service.Shutdown();
                thread.Join(TimeSpan.FromSeconds(2));
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
    }
}
