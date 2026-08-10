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
            WebService service = new(new WebServiceSettings
            {
                Name = "Test Web Service",
                ListenUrl = GetListenUrl(),
                RequestAuthorizer = new RejectingRequestAuthorizer()
            });

            service.RegisterHandler("/test", handler);
            Assert.True(service.Start());

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
            WebService service = new(new WebServiceSettings
            {
                Name = "Test Web Service",
                ListenUrl = GetListenUrl(),
                ExceptionWriter = exceptionWriter
            });

            service.RegisterHandler("/test", handler);
            Assert.True(service.Start());

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
            WebService service = new(new WebServiceSettings
            {
                Name = "Test Web Service",
                ListenUrl = GetListenUrl()
            });

            service.RegisterHandler("/test", handler);
            Assert.True(service.Start());

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
            WebService service = new(new WebServiceSettings
            {
                Name = "Test Web Service",
                ListenUrl = GetListenUrl()
            });

            Assert.True(service.Start());

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

        private static string GetListenUrl()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return $"http://127.0.0.1:{port}/";
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
