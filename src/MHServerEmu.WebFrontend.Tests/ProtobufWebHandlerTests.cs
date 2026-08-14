using System.Net;
using System.Net.Sockets;
using MHServerEmu.Core.Network.Web;
using MHServerEmu.WebFrontend.Handlers;

namespace MHServerEmu.WebFrontend.Tests
{
    public class ProtobufWebHandlerTests
    {
        [Fact]
        public async Task Post_WithoutUserAgent_ReturnsForbidden()
        {
            WebService service = StartWithRetry();

            try
            {
                using HttpClient client = new();

                HttpResponseMessage response = await client.PostAsync($"{service.Settings.ListenUrl}Login/IndexPB", new ByteArrayContent(Array.Empty<byte>()));

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        private static WebService StartWithRetry()
        {
            HttpListenerException lastException = null;

            for (int attempt = 0; attempt < 5; attempt++)
            {
                int port;
                using (TcpListener listener = new(IPAddress.Loopback, 0))
                {
                    listener.Start();
                    port = ((IPEndPoint)listener.LocalEndpoint).Port;
                }

                WebService service = CreateService(port);
                service.RegisterHandler("/Login/IndexPB", new ProtobufWebHandler(true, TimeSpan.FromSeconds(1), 1, 10));

                try
                {
                    service.Start();
                    return service;
                }
                catch (HttpListenerException e)
                {
                    if (service.IsRunning)
                        service.Stop();

                    lastException = e;
                }
            }

            throw lastException;
        }

        private static WebService CreateService(int port)
        {
            return new(new WebServiceSettings
            {
                Name = "Test",
                ListenUrl = $"http://127.0.0.1:{port}/",
                FallbackHandler = null,
            });
        }
    }
}
