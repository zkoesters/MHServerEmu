using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.PortalBridge.Authentication;
using MHServerEmu.PortalBridge.Handlers;
using MHServerEmu.PortalBridge.Models;

namespace MHServerEmu.PortalBridge.Tests.Handlers
{
    public class ResponseModelTests
    {
        [Fact]
        public void CapabilitiesResponse_SerializesExactContractFields()
        {
            BridgeCapabilitiesResponse response = new("1.0", "1.0.2", new string('a', 40), "1.52.0.1700", 1,
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"), new[] { "bridge.health" });

            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response));

            Assert.Equal(new[] { "contractVersion", "emulatorVersion", "upstreamCommit", "gameBuild", "snapshotSchemaVersion", "serverInstanceId", "capabilities" },
                json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
            Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
        }

        [Theory]
        [InlineData(GameServiceState.Running, "healthy", "healthy")]
        [InlineData(GameServiceState.Starting, "degraded", "degraded")]
        [InlineData(GameServiceState.ShuttingDown, "degraded", "degraded")]
        [InlineData(GameServiceState.Created, "unhealthy", "unhealthy")]
        [InlineData(GameServiceState.Shutdown, "unhealthy", "unhealthy")]
        public void CreateResponse_PlayerManagerState_MapsExactHealth(GameServiceState state, string playerManagerStatus,
            string aggregateStatus)
        {
            BridgeHealthResponse response = HealthWebHandler.CreateResponse(state, DateTimeOffset.UnixEpoch);

            Assert.Equal(aggregateStatus, response.Status);
            Assert.Equal(DateTimeOffset.UnixEpoch, response.CheckedAtUtc);
            Assert.Equal(new[] { "bridge", "playerManager" }, response.Services.Keys);
            Assert.Equal("healthy", response.Services["bridge"]);
            Assert.Equal(playerManagerStatus, response.Services["playerManager"]);
        }

        [Fact]
        public void CreateResponse_MissingPlayerManager_MapsUnhealthy()
        {
            BridgeHealthResponse response = HealthWebHandler.CreateResponse(null, DateTimeOffset.UnixEpoch);

            Assert.Equal("unhealthy", response.Status);
            Assert.Equal("unhealthy", response.Services["playerManager"]);
        }

        [Fact]
        public void ProblemResponse_SerializesOnlyCodeAndCorrelationId()
        {
            BridgeProblemResponse response = new("bridge_internal_error",
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"));

            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(response));

            Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("bridge_internal_error", json.RootElement.GetProperty("code").GetString());
        }

        [Fact]
        public async Task RequestAuthorizer_InvalidRequest_ReturnsSanitizedProblemResponse()
        {
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();
            WebService service = StartService(listenUrl => new()
            {
                Name = "Portal Bridge Test",
                ListenUrl = listenUrl,
                RequestAuthorizer = new PortalBridgeRequestAuthorizer(validator),
            });

            try
            {
                Guid operationId = Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
                using HttpClient client = new();
                using HttpRequestMessage request = new(HttpMethod.Get, service.Settings.ListenUrl + "test");
                request.Headers.Add(HmacRequestValidator.OperationIdHeader, operationId.ToString("D"));
                using HttpResponseMessage response = await client.SendAsync(request);
                string body = await response.Content.ReadAsStringAsync();
                using JsonDocument json = JsonDocument.Parse(body);

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType.MediaType);
                Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
                Assert.Equal("bridge_authentication_failed", json.RootElement.GetProperty("code").GetString());
                Assert.True(Guid.TryParse(json.RootElement.GetProperty("correlationId").GetString(), out Guid correlationId));
                Assert.NotEqual(operationId, correlationId);
                Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task ExceptionWriter_HandlerFails_ReturnsSanitizedProblemResponse()
        {
            WebService service = StartService(listenUrl => new()
            {
                Name = "Portal Bridge Test",
                ListenUrl = listenUrl,
                ExceptionWriter = new PortalBridgeExceptionWriter(),
            }, service => service.RegisterHandler("/throw", new ThrowingWebHandler()));

            try
            {
                using HttpClient client = new();
                using HttpResponseMessage response = await client.GetAsync(service.Settings.ListenUrl + "throw");
                string body = await response.Content.ReadAsStringAsync();
                using JsonDocument json = JsonDocument.Parse(body);

                Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType.MediaType);
                Assert.Equal(new[] { "code", "correlationId" }, json.RootElement.EnumerateObject().Select(property => property.Name));
                Assert.Equal("bridge_internal_error", json.RootElement.GetProperty("code").GetString());
                Assert.True(Guid.TryParse(json.RootElement.GetProperty("correlationId").GetString(), out _));
                Assert.DoesNotContain("expected raw exception", body, StringComparison.Ordinal);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task CapabilitiesHandler_QueryRoute_ReturnsNotFound()
        {
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");
            WebService service = StartService(listenUrl => new()
            {
                Name = "Portal Bridge Test",
                ListenUrl = listenUrl,
                FallbackHandler = new PortalBridgeNotFoundWebHandler(),
            }, service => service.RegisterHandler(CapabilitiesWebHandler.Path,
                new CapabilitiesWebHandler(metadata, Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"))));

            try
            {
                using HttpClient client = new();
                using HttpResponseMessage response = await client.GetAsync(service.Settings.ListenUrl + "portal-bridge/v1/capabilities?extra=true");

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task NotFoundHandler_GetRequest_ReturnsNotFound()
        {
            WebService service = StartService(listenUrl => new()
            {
                Name = "Portal Bridge Test",
                ListenUrl = listenUrl,
                FallbackHandler = new PortalBridgeNotFoundWebHandler(),
            });

            try
            {
                using HttpClient client = new();
                using HttpResponseMessage response = await client.GetAsync(service.Settings.ListenUrl + "missing");

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        private static WebService StartService(Func<string, WebServiceSettings> settingsFactory, Action<WebService> configure = null)
        {
            const int MaxAttempts = 10;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                WebService service = new(settingsFactory(GetListenUrl()));
                configure?.Invoke(service);

                try
                {
                    if (service.Start())
                        return service;
                }
                catch (HttpListenerException)
                {
                }
            }

            throw new InvalidOperationException($"Failed to start test web service after {MaxAttempts} attempts");
        }

        private static string GetListenUrl()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}/";
        }

        private sealed class ThrowingWebHandler : WebHandler
        {
            protected override Task Get(WebRequestContext context)
            {
                throw new InvalidOperationException("expected raw exception");
            }
        }
    }
}
