using System.Net;
using System.Net.Sockets;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
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

        [Fact]
        public async Task Post_PrecacheRequests_DoNotConsumeLoginSourceLimit()
        {
            ProtocolDispatchTable.Instance.Initialize();
            WebService service = StartWithRetry();

            try
            {
                using HttpClient client = new();
                PrecacheHeaders precacheHeaders = PrecacheHeaders.CreateBuilder().SetLocale("en-US").Build();
                byte[] precacheRequest = CreateRequest(FrontendProtocolMessage.PrecacheHeaders, precacheHeaders);

                for (int attempt = 0; attempt < 2; attempt++)
                {
                    HttpResponseMessage response = await PostGameClientAsync(client, service.Settings.ListenUrl, precacheRequest);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                }

                LoginDataPB loginData = LoginDataPB.CreateBuilder().SetEmailAddress(string.Empty).SetPassword("password").Build();
                byte[] loginRequest = CreateRequest(FrontendProtocolMessage.LoginDataPB, loginData);
                HttpResponseMessage loginResponse = await PostGameClientAsync(client, service.Settings.ListenUrl, loginRequest);

                Assert.Equal(HttpStatusCode.BadRequest, loginResponse.StatusCode);
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

        private static async Task<HttpResponseMessage> PostGameClientAsync(HttpClient client, string listenUrl, byte[] body)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, $"{listenUrl}Login/IndexPB")
            {
                Content = new ByteArrayContent(body),
            };
            request.Headers.UserAgent.ParseAdd("Secret Identity Studios Http Client");
            return await client.SendAsync(request);
        }

        private static byte[] CreateRequest(FrontendProtocolMessage messageType, IMessage message)
        {
            byte[] body = new byte[CodedOutputStream.ComputeRawVarint32Size((uint)messageType)
                + CodedOutputStream.ComputeRawVarint32Size((uint)message.SerializedSize) + message.SerializedSize];
            CodedOutputStream output = CodedOutputStream.CreateInstance(body);
            output.WriteRawVarint32((uint)messageType);
            output.WriteRawVarint32((uint)message.SerializedSize);
            message.WriteTo(output);
            output.Flush();
            return body;
        }
    }
}
