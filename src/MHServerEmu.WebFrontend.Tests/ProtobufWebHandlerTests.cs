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
            int port;
            using (TcpListener listener = new(IPAddress.Loopback, 0))
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            WebService service = new(new WebServiceSettings
            {
                Name = "Test",
                ListenUrl = $"http://127.0.0.1:{port}/",
                FallbackHandler = null,
            });
            service.RegisterHandler("/Login/IndexPB", new ProtobufWebHandler(false, TimeSpan.FromSeconds(1), 1));

            try
            {
                service.Start();
                using HttpClient client = new();

                HttpResponseMessage response = await client.PostAsync($"{service.Settings.ListenUrl}Login/IndexPB", new ByteArrayContent(Array.Empty<byte>()));

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }
    }
}
