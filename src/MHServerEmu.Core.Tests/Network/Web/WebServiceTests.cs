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

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

                HttpResponseMessage failedResponse = await client.GetAsync($"{service.Settings.ListenUrl}test");
                HttpResponseMessage successfulResponse = await client.GetAsync($"{service.Settings.ListenUrl}test");

                Assert.Equal(HttpStatusCode.InternalServerError, failedResponse.StatusCode);
                Assert.Equal(HttpStatusCode.OK, successfulResponse.StatusCode);
                Assert.Equal("success", await successfulResponse.Content.ReadAsStringAsync());
            }
            finally
            {
                service.Stop();
            }
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
            public Task<bool> AuthorizeAsync(WebRequestContext context)
            {
                return Task.FromResult(false);
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
    }
}
