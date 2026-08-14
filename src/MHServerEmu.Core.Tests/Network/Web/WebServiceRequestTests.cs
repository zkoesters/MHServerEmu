using System.Net;
using System.Net.Sockets;
using System.Text;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.Core.Tests.Network.Web
{
    public class WebServiceRequestTests
    {
        [Fact]
        public async Task Post_MalformedJson_ReturnsBadRequest()
        {
            WebService service = CreateService();

            try
            {
                service.Start();
                using HttpClient client = new();

                HttpResponseMessage response = await client.PostAsync(service.Settings.ListenUrl, new StringContent("{", Encoding.UTF8, "application/json"));

                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task Post_OversizedBody_ReturnsPayloadTooLarge()
        {
            WebService service = CreateService();

            try
            {
                service.Start();
                using HttpClient client = new();

                HttpResponseMessage response = await client.PostAsync(service.Settings.ListenUrl, new ByteArrayContent(new byte[17]));

                Assert.Equal((HttpStatusCode)413, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        private static WebService CreateService()
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
                MaxRequestBodyBytes = 16,
                RequestBodyReadTimeout = TimeSpan.FromSeconds(1),
            });
            service.RegisterHandler("/", new JsonHandler());
            return service;
        }

        private sealed class JsonHandler : WebHandler
        {
            protected override async Task Post(WebRequestContext context)
            {
                await context.ReadJsonAsync<Payload>();
            }
        }

        private sealed class Payload
        {
            public string Value { get; set; }
        }
    }
}
