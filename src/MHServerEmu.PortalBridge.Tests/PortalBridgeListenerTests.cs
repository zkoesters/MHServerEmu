using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MHServerEmu.Core.Network;
using MHServerEmu.PortalBridge.Handlers;

namespace MHServerEmu.PortalBridge.Tests
{
    [Collection("PortalBridge logging")]
    public class PortalBridgeListenerTests
    {
        [Fact]
        public async Task GetCapabilities_ValidSignature_ReturnsExactPayload()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpClient client = bridge.CreateSignedClient();

            using HttpResponseMessage response = await client.GetAsync(CapabilitiesWebHandler.Path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal("1.0.2", json.RootElement.GetProperty("emulatorVersion").GetString());
            Assert.Equal(PortalBridgeBuildMetadata.UpstreamCommit, json.RootElement.GetProperty("upstreamCommit").GetString());
            Assert.Equal("1.52.0.1700", json.RootElement.GetProperty("gameBuild").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
            Assert.Equal(Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"),
                json.RootElement.GetProperty("serverInstanceId").GetGuid());
            Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        }

        [Fact]
        public async Task GetCapabilities_ReplayedSignature_ReturnsUnauthorized()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage first = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);
            using HttpRequestMessage replay = bridge.CloneRequest(first);

            using HttpResponseMessage accepted = await bridge.Client.SendAsync(first);
            using HttpResponseMessage rejected = await bridge.Client.SendAsync(replay);

            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        [Fact]
        public async Task SignedQuery_DoesNotMatchContractRoute()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path + "?extra=true");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task GetHealth_ValidSignature_ReturnsHealthyServices()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(HealthWebHandler.Path);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "status", "checkedAtUtc", "services" },
                json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
            Assert.True(DateTimeOffset.TryParse(json.RootElement.GetProperty("checkedAtUtc").GetString(), out _));
            Assert.Equal(new[] { "bridge", "playerManager" },
                json.RootElement.GetProperty("services").EnumerateObject().Select(property => property.Name));
            Assert.Equal("healthy", json.RootElement.GetProperty("services").GetProperty("bridge").GetString());
            Assert.Equal("healthy", json.RootElement.GetProperty("services").GetProperty("playerManager").GetString());
        }

        [Fact]
        public async Task SignedUnknownPath_ReturnsNotFound()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest("/portal-bridge/v1/unknown");

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        [Fact]
        public async Task SignedPost_ReturnsMethodNotAllowed()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path, HttpMethod.Post);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        }

        [Fact]
        public async Task TamperedPost_ReturnsUnauthorizedBeforeMethodDispatch()
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path, HttpMethod.Post);
            request.Headers.Remove("X-Portal-Signature");
            request.Headers.TryAddWithoutValidation("X-Portal-Signature", new string('0', 64));

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        [Fact]
        public void Start_BindFailure_RetriesOnNewLoopbackPort()
        {
            using TcpListener occupied = new(IPAddress.Loopback, 0);
            occupied.Start();
            int occupiedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
            int attempts = 0;

            using RunningBridge bridge = RunningBridge.Start(GameServiceState.Running, () =>
            {
                attempts++;
                return attempts == 1 ? occupiedPort : GetFreePort();
            });

            Assert.True(attempts >= 2);
        }

        [Fact]
        public async Task GetCapabilities_IPv6Loopback_ReturnsExactPayloadWhenAvailable()
        {
            if (CanStartIPv6LoopbackHttpListener() == false)
                return;

            using RunningBridge bridge = RunningBridge.Start("::1");
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[]
                {
                    "contractVersion",
                    "emulatorVersion",
                    "upstreamCommit",
                    "gameBuild",
                    "snapshotSchemaVersion",
                    "serverInstanceId",
                    "capabilities",
                }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal("1.0.2", json.RootElement.GetProperty("emulatorVersion").GetString());
            Assert.Equal(PortalBridgeBuildMetadata.UpstreamCommit, json.RootElement.GetProperty("upstreamCommit").GetString());
            Assert.Equal("1.52.0.1700", json.RootElement.GetProperty("gameBuild").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
            Assert.Equal(Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"),
                json.RootElement.GetProperty("serverInstanceId").GetGuid());
            Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task MissingOrTamperedSignature_ReturnsExactAuthenticationProblem(bool missingSignature)
        {
            using RunningBridge bridge = RunningBridge.Start();
            using HttpRequestMessage request = bridge.CreateSignedRequest(CapabilitiesWebHandler.Path);
            request.Headers.Remove("X-Portal-Signature");
            if (missingSignature == false)
                request.Headers.TryAddWithoutValidation("X-Portal-Signature", new string('0', 64));

            using HttpResponseMessage response = await bridge.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType.MediaType);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("bridge_authentication_failed", json.RootElement.GetProperty("code").GetString());
            Assert.NotEqual(Guid.Empty, json.RootElement.GetProperty("correlationId").GetGuid());
        }

        private static int GetFreePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static bool CanStartIPv6LoopbackHttpListener()
        {
            if (Socket.OSSupportsIPv6 == false)
                return false;

            try
            {
                using TcpListener reservation = new(IPAddress.IPv6Loopback, 0);
                reservation.Start();
                int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
                reservation.Stop();

                using HttpListener listener = new();
                listener.Prefixes.Add($"http://[::1]:{port}/");
                listener.Start();
                return true;
            }
            catch (HttpListenerException)
            {
                return false;
            }
            catch (SocketException)
            {
                return false;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}
