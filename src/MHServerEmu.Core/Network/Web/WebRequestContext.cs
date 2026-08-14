using System.Buffers;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using Google.ProtocolBuffers;

namespace MHServerEmu.Core.Network.Web
{
    /// <summary>
    /// Wrapper for <see cref="HttpListenerContext"/>.
    /// </summary>
    public readonly struct WebRequestContext
    {
        private readonly HttpListenerRequest _httpRequest;
        private readonly HttpListenerResponse _httpResponse;
        private readonly WebServiceSettings _settings;
        private readonly ClientIpResult _clientIp;

        public string UserAgent { get => _httpRequest.UserAgent; }
        public string LocalPath { get => _httpRequest.Url.LocalPath; }
        public string HttpMethod { get => _httpRequest.HttpMethod; }
        public string Authorization { get => _httpRequest.Headers["Authorization"]; }
        public bool IsForwardedRequest { get => _clientIp.IsForwarded; }

        public bool IsGameClientRequest { get => string.Equals(UserAgent, "Secret Identity Studios Http Client", StringComparison.InvariantCulture); }

        public int StatusCode { get => _httpResponse.StatusCode; set => _httpResponse.StatusCode = value; }

        public WebRequestContext(HttpListenerContext httpContext) : this(httpContext, new WebServiceSettings()) { }

        public WebRequestContext(HttpListenerContext httpContext, WebServiceSettings settings)
        {
            _httpRequest = httpContext.Request;
            _httpResponse = httpContext.Response;
            _settings = settings;
            _clientIp = ClientIpResolver.Resolve(_httpRequest.RemoteEndPoint.Address, _httpRequest.Headers["X-Forwarded-For"],
                settings.TrustedProxyNetworks, settings.MaxForwardedForHeaderLength, settings.MaxForwardedForHops);

            _httpResponse.StatusCode = 200;
            _httpResponse.KeepAlive = false;
        }

        public override string ToString()
        {
            return $"{HttpMethod} {LocalPath}";
        }

        public string GetIPAddress()
        {
            return _clientIp.Address.ToString();
        }

        public string GetBearerToken()
        {
            string authorization = Authorization;
            if (string.IsNullOrWhiteSpace(authorization))
                return null;

            string[] data = authorization.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (data == null || data.Length < 2)
                return null;

            if (data[0].Equals("Bearer", StringComparison.OrdinalIgnoreCase) == false)
                return null;

            return data[1];
        }

        public void Redirect(string url)
        {
            _httpResponse.Redirect(url);
        }

        /// <summary>
        /// Asynchronously reads the request input stream as a UTF-8 string.
        /// </summary>
        public async Task<string> ReadUtf8StringAsync()
        {
            byte[] body = await BoundedRequestBodyReader.ReadAsync(_httpRequest.InputStream, _httpRequest.ContentLength64,
                _settings.MaxRequestBodyBytes, _settings.RequestBodyReadTimeout);
            return Encoding.UTF8.GetString(body);
        }

        /// <summary>
        /// Asynchronously reads the request input stream as <typeparamref name="T"/> serialized using JSON.
        /// </summary>
        public async Task<T> ReadJsonAsync<T>()
        {
            byte[] body = await BoundedRequestBodyReader.ReadAsync(_httpRequest.InputStream, _httpRequest.ContentLength64,
                _settings.MaxRequestBodyBytes, _settings.RequestBodyReadTimeout);

            try
            {
                return JsonSerializer.Deserialize<T>(body, new JsonSerializerOptions { MaxDepth = _settings.JsonMaxDepth });
            }
            catch (JsonException e)
            {
                throw new WebRequestException(HttpStatusCode.BadRequest, $"Malformed JSON request body: {e.Message}");
            }
        }

        /// <summary>
        /// Asynchronously reads the request input stream as a <see cref="NameValueCollection"/>.
        /// </summary>
        public async Task<NameValueCollection> ReadQueryStringAsync()
        {
            string queryString = await ReadUtf8StringAsync();
            return HttpUtility.ParseQueryString(queryString);
        }

        /// <summary>
        /// Reads the request input stream as an <see cref="IMessage"/> of protocol <typeparamref name="T"/>.
        /// </summary>
        [Obsolete("Use ReadProtobufAsync<T>() for bounded asynchronous request reads.")]
        public IMessage ReadProtobuf<T>() where T: Enum
        {
            return ReadProtobufAsync<T>().GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously reads the request input stream as an <see cref="IMessage"/> of protocol <typeparamref name="T"/>.
        /// </summary>
        public async Task<IMessage> ReadProtobufAsync<T>() where T: Enum
        {
            byte[] body = await BoundedRequestBodyReader.ReadAsync(_httpRequest.InputStream, _httpRequest.ContentLength64,
                _settings.MaxRequestBodyBytes, _settings.RequestBodyReadTimeout);
            using MemoryStream stream = new(body, writable: false);
            MessageBuffer messageBuffer = new(stream);
            IMessage message = messageBuffer.Deserialize<T>();

            if (message == null)
                throw new WebRequestException(HttpStatusCode.BadRequest, "Malformed protobuf request body.");

            return message;
        }

        /// <summary>
        /// Asynchronously responds to the request with the provided payload.
        /// </summary>
        public async Task SendAsync(byte[] payload, string contentType)
        {
            _httpResponse.ContentType = contentType;
            _httpResponse.ContentLength64 = payload.Length;

            await _httpResponse.OutputStream.WriteAsync(payload);
        }

        /// <summary>
        /// Asynchronously responds to the request with the provided <see cref="string"/> encoded as UTF-8.
        /// </summary>
        public async Task SendAsync(string message, string contentType = "text/plain")
        {
            int maxByteCount = Encoding.UTF8.GetMaxByteCount(message.Length);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(maxByteCount);

            try
            {
                int byteCount = Encoding.UTF8.GetBytes(message, 0, message.Length, buffer, 0);

                _httpResponse.ContentType = contentType;
                _httpResponse.ContentLength64 = byteCount;

                await _httpResponse.OutputStream.WriteAsync(buffer.AsMemory(0, byteCount));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        
        /// <summary>
        /// Asynchronously responds to the request with the provided <see cref="IMessage"/>.
        /// </summary>
        public async Task SendAsync(IMessage message)
        {
            MessagePackageOut payload = new(message);
            int size = payload.GetSerializedSize();

            byte[] buffer = ArrayPool<byte>.Shared.Rent(size);

            try
            {
                CodedOutputStream cos = CodedOutputStream.CreateInstance(buffer);
                payload.WriteTo(cos);
                cos.Flush();

                _httpResponse.ContentType = "application/octet-stream";
                _httpResponse.ContentLength64 = size;

                await _httpResponse.OutputStream.WriteAsync(buffer.AsMemory(0, size));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Asynchronously responds to the request with the provided <typeparamref name="T"/> instance serialized to JSON.
        /// </summary>
        public async Task SendJsonAsync<T>(T @object)
        {
            _httpResponse.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(_httpResponse.OutputStream, @object);
        }
    }
}
