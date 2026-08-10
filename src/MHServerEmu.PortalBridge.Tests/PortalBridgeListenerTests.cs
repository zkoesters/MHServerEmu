using System.Net;
using System.Text.Json;
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
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal(PortalBridgeBuildMetadata.UpstreamCommit, json.RootElement.GetProperty("upstreamCommit").GetString());
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
            Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
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
    }
}
