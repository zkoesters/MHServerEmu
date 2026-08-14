using System.Net;
using System.Net.Sockets;
using System.Text;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Network;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.Core.Tests.Network.Web
{
    public class WebServiceRequestTests
    {
        [Fact]
        public async Task Post_MalformedJson_ReturnsBadRequest()
        {
            WebService service = StartWithRetry(("/", new JsonHandler()));

            try
            {
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
            WebService service = StartWithRetry(("/", new JsonHandler()));

            try
            {
                using HttpClient client = new();

                HttpResponseMessage response = await client.PostAsync(service.Settings.ListenUrl, new ByteArrayContent(new byte[17]));

                Assert.Equal((HttpStatusCode)413, response.StatusCode);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task Post_ProtobufReaders_DeserializeSameMessageType()
        {
            ProtocolDispatchTable.Instance.Initialize();
            SyncProtobufHandler syncHandler = new();
            AsyncProtobufHandler asyncHandler = new();
            WebService service = StartWithRetry(("/sync", syncHandler), ("/async", asyncHandler));

            try
            {
                using HttpClient client = new();

                HttpResponseMessage syncResponse = await client.PostAsync($"{service.Settings.ListenUrl}sync", new ByteArrayContent(CreateLoginDataRequest()));
                HttpResponseMessage asyncResponse = await client.PostAsync($"{service.Settings.ListenUrl}async", new ByteArrayContent(CreateLoginDataRequest()));

                Assert.Equal(HttpStatusCode.OK, syncResponse.StatusCode);
                Assert.Equal(HttpStatusCode.OK, asyncResponse.StatusCode);
                Assert.IsType<LoginDataPB>(syncHandler.Message);
                Assert.IsType<LoginDataPB>(asyncHandler.Message);
            }
            finally
            {
                service.Stop();
            }
        }

        [Fact]
        public async Task WebRequestContext_OriginalConstructor_UsesDefaultSettings()
        {
            int port = GetFreePort();
            string prefix = $"http://127.0.0.1:{port}/";
            using HttpListener listener = new();
            listener.Prefixes.Add(prefix);
            listener.Start();
            using HttpClient client = new();
            using HttpRequestMessage request = new(HttpMethod.Get, prefix);
            request.Headers.Add("X-Forwarded-For", "198.51.100.20");

            Task<HttpResponseMessage> responseTask = client.SendAsync(request);
            HttpListenerContext httpContext = await listener.GetContextAsync();
            WebRequestContext requestContext = new(httpContext);
            httpContext.Response.Close();

            HttpResponseMessage response = await responseTask;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(200, requestContext.StatusCode);
#pragma warning disable CS0618
            Assert.Equal("198.51.100.20", requestContext.XForwardedFor);
#pragma warning restore CS0618
            Assert.Equal(IPAddress.Loopback.ToString(), requestContext.GetIPAddress());
            Assert.False(requestContext.IsForwardedRequest);
        }

        [Fact]
        public void Start_FailedListenerStart_CanRetryAfterPortReleased()
        {
            int port = GetFreePort();
            string prefix = $"http://127.0.0.1:{port}/";
            using HttpListener reservedListener = new();
            reservedListener.Prefixes.Add(prefix);
            reservedListener.Start();

            WebService service = CreateService(port);

            try
            {
                Assert.Throws<HttpListenerException>(() => service.Start());
                Assert.False(service.IsRunning);

                reservedListener.Close();

                Assert.True(service.Start());
                Assert.True(service.Stop());
            }
            finally
            {
                service.Stop();
            }
        }

        private static WebService StartWithRetry(params (string LocalPath, WebHandler Handler)[] handlers)
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
                foreach ((string localPath, WebHandler handler) in handlers)
                    service.RegisterHandler(localPath, handler);

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

        private static int GetFreePort()
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static WebService CreateService(int port)
        {
            return new(new WebServiceSettings
            {
                Name = "Test",
                ListenUrl = $"http://127.0.0.1:{port}/",
                FallbackHandler = null,
                MaxRequestBodyBytes = 16,
                RequestBodyReadTimeout = TimeSpan.FromSeconds(1),
            });
        }

        private sealed class JsonHandler : WebHandler
        {
            protected override async Task Post(WebRequestContext context)
            {
                await context.ReadJsonAsync<Payload>();
            }
        }

        private sealed class SyncProtobufHandler : WebHandler
        {
            public IMessage Message { get; private set; }

            protected override Task Post(WebRequestContext context)
            {
#pragma warning disable CS0618
                Message = context.ReadProtobuf<FrontendProtocolMessage>();
#pragma warning restore CS0618
                return Task.CompletedTask;
            }
        }

        private sealed class AsyncProtobufHandler : WebHandler
        {
            public IMessage Message { get; private set; }

            protected override async Task Post(WebRequestContext context)
            {
                Message = await context.ReadProtobufAsync<FrontendProtocolMessage>();
            }
        }

        private static byte[] CreateLoginDataRequest()
        {
            LoginDataPB message = LoginDataPB.CreateBuilder().SetEmailAddress("e").SetPassword("p").Build();
            byte[] body = new byte[1 + CodedOutputStream.ComputeRawVarint32Size((uint)message.SerializedSize) + message.SerializedSize];
            CodedOutputStream output = CodedOutputStream.CreateInstance(body);
            output.WriteRawVarint32((uint)FrontendProtocolMessage.LoginDataPB);
            output.WriteRawVarint32((uint)message.SerializedSize);
            message.WriteTo(output);
            output.Flush();
            return body;
        }

        private sealed class Payload
        {
            public string Value { get; set; }
        }
    }
}
