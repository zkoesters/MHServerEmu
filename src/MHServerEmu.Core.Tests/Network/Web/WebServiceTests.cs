using System.Net;
using System.Net.Sockets;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.Core.Tests.Network.Web
{
    public class WebServiceTests
    {
        [Fact]
        public async Task HandleRequest_AuthorizerRejectsRequest_DoesNotInvokeHandler()
        {
            CountingWebHandler handler = new();
            WebService service = StartService(
                listenUrl => new()
                {
                    Name = "Test Web Service",
                    ListenUrl = listenUrl,
                    RequestAuthorizer = new RejectingRequestAuthorizer()
                },
                service => service.RegisterHandler("/test", handler));

            try
            {
                using HttpClient client = new();
                HttpResponseMessage response = await client.GetAsync($"{service.Settings.ListenUrl}test");

                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal("rejected", await response.Content.ReadAsStringAsync());
                Assert.Equal(0, handler.RequestCount);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task HandleRequest_HandlerFails_ReturnsErrorAndContinuesServingRequests()
        {
            FailingOnceWebHandler handler = new();
            RecordingExceptionWriter exceptionWriter = new();
            WebService service = StartService(
                listenUrl => new()
                {
                    Name = "Test Web Service",
                    ListenUrl = listenUrl,
                    ExceptionWriter = exceptionWriter
                },
                service => service.RegisterHandler("/test", handler));

            try
            {
                using HttpClient client = new();

                HttpResponseMessage failedResponse = await client.GetAsync($"{service.Settings.ListenUrl}test");
                HttpResponseMessage successfulResponse = await client.GetAsync($"{service.Settings.ListenUrl}test");

                Assert.Equal(HttpStatusCode.InternalServerError, failedResponse.StatusCode);
                Assert.Equal(1, exceptionWriter.WriteCount);
                Assert.Equal(HttpStatusCode.OK, successfulResponse.StatusCode);
                Assert.Equal("success", await successfulResponse.Content.ReadAsStringAsync());
                Assert.True(service.IsRunning);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task HandleRequest_HeaderIsAbsent_ReturnsEmptyHeaderValues()
        {
            HeaderValuesWebHandler handler = new();
            WebService service = StartService(
                listenUrl => new()
                {
                    Name = "Test Web Service",
                    ListenUrl = listenUrl
                },
                service => service.RegisterHandler("/test", handler));

            try
            {
                using HttpClient client = new();
                HttpResponseMessage response = await client.GetAsync($"{service.Settings.ListenUrl}test");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Empty(handler.HeaderValues);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task HandleRequest_NoMatchingHandler_PreservesDefaultResponse()
        {
            WebService service = StartService(listenUrl => new()
            {
                Name = "Test Web Service",
                ListenUrl = listenUrl
            });

            try
            {
                using HttpClient client = new();
                HttpResponseMessage response = await client.GetAsync($"{service.Settings.ListenUrl}test");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public void Start_ListenerFailsToStart_CanStartAfterListenerIsReleased()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            WebService service = new(new WebServiceSettings
            {
                Name = "Test Web Service",
                ListenUrl = $"http://127.0.0.1:{port}/"
            });

            Assert.Throws<HttpListenerException>(() => service.Start());
            Assert.False(service.IsRunning);

            listener.Stop();

            Assert.True(service.Start());
            Assert.True(service.Stop());
        }

        [Fact]
        public async Task StopAndRestart_BlockedOldRequest_DoesNotClearNewServiceState()
        {
            BlockingRequestAuthorizer authorizer = new();
            CountingWebHandler handler = new();
            WebService service = StartService(
                listenUrl => new()
                {
                    Name = "Test Web Service",
                    ListenUrl = listenUrl,
                    RequestAuthorizer = authorizer
                },
                service => service.RegisterHandler("/test", handler));

            try
            {
                using HttpClient client = new();
                Task<HttpResponseMessage> blockedRequest = client.GetAsync($"{service.Settings.ListenUrl}test");
                await authorizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(service.Stop());
                Assert.True(service.Start());

                authorizer.Release.SetResult(true);
                Assert.True(SpinWait.SpinUntil(() => service.HandledRequests == 1, TimeSpan.FromSeconds(5)));

                Assert.True(service.IsRunning);

                HttpResponseMessage response = await client.GetAsync($"{service.Settings.ListenUrl}test");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                await Record.ExceptionAsync(async () => await blockedRequest);
            }
            finally
            {
                authorizer.Release.TrySetResult(true);
                service.Stop();
            }
        }

        [Fact]
        public async Task StopAsync_BlockedAcceptedRequest_WaitsForRequestCompletion()
        {
            BlockingRequestAuthorizer authorizer = new();
            WebService service = StartService(
                listenUrl => new()
                {
                    Name = "Test Web Service",
                    ListenUrl = listenUrl,
                    RequestAuthorizer = authorizer
                },
                service => service.RegisterHandler("/test", new CountingWebHandler()));

            try
            {
                using HttpClient client = new();
                Task<HttpResponseMessage> request = client.GetAsync($"{service.Settings.ListenUrl}test");
                await authorizer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Task stop = service.StopAsync();
                Assert.False(stop.IsCompleted);

                authorizer.Release.TrySetResult(true);
                await stop.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(service.IsRunning);
                await Record.ExceptionAsync(async () => await request);
            }
            finally
            {
                authorizer.Release.TrySetResult(true);
                await service.StopAsync();
            }
        }

        private static string GetListenUrl()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}/";
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

        private sealed class RejectingRequestAuthorizer : IWebRequestAuthorizer
        {
            public async Task<bool> AuthorizeAsync(WebRequestContext context)
            {
                context.StatusCode = (int)HttpStatusCode.Unauthorized;
                await context.SendAsync("rejected");
                return false;
            }
        }

        private sealed class RecordingExceptionWriter : IWebExceptionWriter
        {
            public int WriteCount { get; private set; }

            public Task WriteAsync(WebRequestContext context, Exception exception)
            {
                WriteCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class BlockingRequestAuthorizer : IWebRequestAuthorizer
        {
            public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async Task<bool> AuthorizeAsync(WebRequestContext context)
            {
                Entered.TrySetResult(true);
                await Release.Task;
                return false;
            }
        }

        private sealed class CountingWebHandler : WebHandler
        {
            public int RequestCount { get; private set; }

            protected override Task Get(WebRequestContext context)
            {
                RequestCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FailingOnceWebHandler : WebHandler
        {
            private bool _shouldFail = true;

            protected override async Task Get(WebRequestContext context)
            {
                if (_shouldFail)
                {
                    _shouldFail = false;
                    throw new InvalidOperationException();
                }

                await context.SendAsync("success");
            }
        }

        private sealed class HeaderValuesWebHandler : WebHandler
        {
            public string[] HeaderValues { get; private set; }

            protected override Task Get(WebRequestContext context)
            {
                HeaderValues = context.GetHeaderValues("X-Test-Header");
                return Task.CompletedTask;
            }
        }
    }
}
