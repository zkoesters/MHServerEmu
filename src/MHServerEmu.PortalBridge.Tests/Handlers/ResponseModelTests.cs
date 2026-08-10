using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MHServerEmu.Core.Logging;
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
        public async Task CapabilitiesHandler_ValidSignedRequest_ReturnsExactJsonResponse()
        {
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");
            Guid serverInstanceId = Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();
            WebService service = StartBridgeService(validator, metadata, serverInstanceId, GameServiceState.Running,
                DateTimeOffset.UnixEpoch);

            try
            {
                using HttpClient client = new();
                using HttpRequestMessage request = TestRequestFactory.CreateSignedGetRequest(service.Settings.ListenUrl,
                    CapabilitiesWebHandler.Path, "00112233445566778899aabbccddeeff");
                using HttpResponseMessage response = await client.SendAsync(request);
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
                Assert.Equal(new[] { "contractVersion", "emulatorVersion", "upstreamCommit", "gameBuild", "snapshotSchemaVersion", "serverInstanceId", "capabilities" },
                    json.RootElement.EnumerateObject().Select(property => property.Name));
                Assert.Equal("1.0", json.RootElement.GetProperty("contractVersion").GetString());
                Assert.Equal("1.0.2", json.RootElement.GetProperty("emulatorVersion").GetString());
                Assert.Equal(new string('a', 40), json.RootElement.GetProperty("upstreamCommit").GetString());
                Assert.Equal("1.52.0.1700", json.RootElement.GetProperty("gameBuild").GetString());
                Assert.Equal(1, json.RootElement.GetProperty("snapshotSchemaVersion").GetInt32());
                Assert.Equal(serverInstanceId, json.RootElement.GetProperty("serverInstanceId").GetGuid());
                Assert.Equal(new[] { "bridge.health" }, json.RootElement.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()));
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task HealthHandler_ValidSignedRequest_ReturnsExactJsonResponse()
        {
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();
            WebService service = StartBridgeService(validator, metadata,
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"), GameServiceState.Running,
                DateTimeOffset.UnixEpoch);

            try
            {
                using HttpClient client = new();
                using HttpRequestMessage request = TestRequestFactory.CreateSignedGetRequest(service.Settings.ListenUrl,
                    HealthWebHandler.Path, "11112233445566778899aabbccddeeff");
                using HttpResponseMessage response = await client.SendAsync(request);
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType);
                Assert.Equal(new[] { "status", "checkedAtUtc", "services" },
                    json.RootElement.EnumerateObject().Select(property => property.Name));
                Assert.Equal("healthy", json.RootElement.GetProperty("status").GetString());
                Assert.Equal(DateTimeOffset.UnixEpoch, json.RootElement.GetProperty("checkedAtUtc").GetDateTimeOffset());
                JsonElement services = json.RootElement.GetProperty("services");
                Assert.Equal(new[] { "bridge", "playerManager" }, services.EnumerateObject().Select(property => property.Name));
                Assert.Equal("healthy", services.GetProperty("bridge").GetString());
                Assert.Equal("healthy", services.GetProperty("playerManager").GetString());
            }
            finally
            {
                service.Stop();
            }
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
        public async Task HealthHandler_SignedQueryRoute_ReturnsNotFound()
        {
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();
            WebService service = StartBridgeService(validator, metadata,
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"), GameServiceState.Running,
                DateTimeOffset.UnixEpoch);

            try
            {
                using HttpClient client = new();
                using HttpRequestMessage request = TestRequestFactory.CreateSignedGetRequest(service.Settings.ListenUrl,
                    HealthWebHandler.Path + "?extra=true", "21112233445566778899aabbccddeeff");
                using HttpResponseMessage response = await client.SendAsync(request);

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task RequestAuthorizer_SignedQuery_DoesNotLogQueryValue()
        {
            const string queryValue = "untrusted-query-value";
            PortalBridgeMetadata metadata = new("1.0.2", new string('a', 40), "1.52.0.1700");
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();
            WebService service = StartBridgeService(validator, metadata,
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"), GameServiceState.Running,
                DateTimeOffset.UnixEpoch);
            CapturingLogTarget target = new();
            bool loggingEnabled = LogManager.Enabled;
            bool targetAttached = false;
            bool targetDetached = false;
            LogManager.Enabled = true;

            try
            {
                targetAttached = LogManager.AttachTarget(target);
                Assert.True(targetAttached);

                using HttpClient client = new();
                using HttpRequestMessage request = TestRequestFactory.CreateSignedGetRequest(service.Settings.ListenUrl,
                    HealthWebHandler.Path + "?value=" + queryValue, "31112233445566778899aabbccddeeff");
                using HttpResponseMessage response = await client.SendAsync(request);
                LogMessage message = await target.Message.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Contains(HealthWebHandler.Path, message.Message, StringComparison.Ordinal);
                Assert.DoesNotContain(queryValue, message.Message, StringComparison.Ordinal);
            }
            finally
            {
                if (targetAttached)
                    targetDetached = LogManager.DetachTarget(target);

                LogManager.Enabled = loggingEnabled;
                service.Stop();
            }

            Assert.True(targetDetached);
            Assert.False(LogManager.DetachTarget(target));
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

        private static WebService StartBridgeService(HmacRequestValidator validator, PortalBridgeMetadata metadata,
            Guid serverInstanceId, GameServiceState? playerManagerState, DateTimeOffset checkedAtUtc)
        {
            return StartService(listenUrl => new()
            {
                Name = "Portal Bridge Test",
                ListenUrl = listenUrl,
                FallbackHandler = new PortalBridgeNotFoundWebHandler(),
                RequestAuthorizer = new PortalBridgeRequestAuthorizer(validator),
            }, service =>
            {
                service.RegisterHandler(CapabilitiesWebHandler.Path, new CapabilitiesWebHandler(metadata, serverInstanceId));
                service.RegisterHandler(HealthWebHandler.Path,
                    new HealthWebHandler(() => playerManagerState, new FixedTimeProvider(checkedAtUtc)));
            });
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

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedTimeProvider(DateTimeOffset now)
            {
                _now = now;
            }

            public override DateTimeOffset GetUtcNow()
            {
                return _now;
            }
        }

        private sealed class CapturingLogTarget : LogTarget
        {
            public TaskCompletionSource<LogMessage> Message { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CapturingLogTarget() : base(new LogTargetSettings
            {
                MinimumLevel = LoggingLevel.Trace,
                MaximumLevel = LoggingLevel.Fatal,
                Channels = LogChannels.All,
            })
            {
            }

            public override void ProcessLogMessage(in LogMessage message)
            {
                if (message.Logger == nameof(PortalBridgeRequestAuthorizer))
                    Message.TrySetResult(message);
            }
        }
    }
}
