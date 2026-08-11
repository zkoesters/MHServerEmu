using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using MHServerEmu.Core.Network;

namespace MHServerEmu.PortalBridge.Tests
{
    [CollectionDefinition("PortalBridge logging", DisableParallelization = true)]
    public sealed class PortalBridgeLoggingCollection
    {
    }

    internal sealed class RunningBridge : IDisposable
    {
        private const string ContractVersionHeader = "X-Portal-Contract-Version";
        private const string TimestampHeader = "X-Portal-Timestamp";
        private const string NonceHeader = "X-Portal-Nonce";
        private const string BodyDigestHeader = "X-Portal-Body-SHA256";
        private const string OperationIdHeader = "X-Portal-Operation-Id";
        private const string KeyIdHeader = "X-Portal-Key-Id";
        private const string SignatureHeader = "X-Portal-Signature";
        private const string EmptyBodyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        private readonly byte[] _key;
        private readonly string _secretFile;
        private readonly PortalBridgeService _service;
        private readonly Thread _thread;
        private readonly Uri _baseAddress;

        internal HttpClient Client { get; }

        private RunningBridge(byte[] key, string secretFile, PortalBridgeService service, Thread thread, Uri baseAddress)
        {
            _key = key;
            _secretFile = secretFile;
            _service = service;
            _thread = thread;
            _baseAddress = baseAddress;
            Client = new HttpClient { BaseAddress = _baseAddress };
        }

        internal static RunningBridge Start(GameServiceState? playerManagerState = GameServiceState.Running)
        {
            return Start("127.0.0.1", playerManagerState, () => GetFreePort(IPAddress.Loopback));
        }

        internal static RunningBridge Start(GameServiceState? playerManagerState, Func<int> portProvider)
        {
            return Start("127.0.0.1", playerManagerState, portProvider);
        }

        internal static RunningBridge Start(string address, GameServiceState? playerManagerState = GameServiceState.Running)
        {
            IPAddress ipAddress = IPAddress.Parse(address);
            return Start(address, playerManagerState, () => GetFreePort(ipAddress));
        }

        private static RunningBridge Start(string address, GameServiceState? playerManagerState, Func<int> portProvider)
        {
            const int MaxAttempts = 10;

            ArgumentNullException.ThrowIfNull(portProvider);
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                byte[] key = RandomNumberGenerator.GetBytes(32);
                string secretFile = Path.GetTempFileName();
                PortalBridgeService service = null;
                Thread thread = null;
                bool started = false;

                try
                {
                    File.WriteAllText(secretFile, Convert.ToBase64String(key));
                    int port = portProvider();
                    Uri baseAddress = new(PortalBridgeService.FormatListenUrl(address, port));
                    PortalBridgeConfig config = new()
                    {
                        Enabled = true,
                        Address = address,
                        Port = port,
                        KeyId = "portal-primary",
                        SecretFile = secretFile,
                        ServerInstanceId = "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a",
                    };
                    service = new PortalBridgeService(config, new PortalBridgeMetadata("1.0.2", PortalBridgeBuildMetadata.UpstreamCommit,
                        "1.52.0.1700"), () => playerManagerState);
                    thread = new Thread(service.Run) { IsBackground = true };
                    thread.Start();

                    if (SpinWait.SpinUntil(() => service.State == GameServiceState.Running, TimeSpan.FromSeconds(2)) &&
                        service.IsAvailable)
                    {
                        started = true;
                        return new RunningBridge(key, secretFile, service, thread, baseAddress);
                    }
                }
                finally
                {
                    if (started == false)
                        StopAttempt(service, thread, key, secretFile);
                }
            }

            throw new InvalidOperationException($"Failed to start PortalBridge test listener after {MaxAttempts} attempts.");
        }

        internal HttpClient CreateSignedClient()
        {
            return new HttpClient(new SigningHandler(_key)) { BaseAddress = _baseAddress };
        }

        internal HttpRequestMessage CreateSignedRequest(string rawUrl, HttpMethod method = null, string nonce = null)
        {
            HttpRequestMessage request = new(method ?? HttpMethod.Get, new Uri(_baseAddress, rawUrl));
            AddSignature(request, _key, nonce);
            return request;
        }

        internal HttpRequestMessage CloneRequest(HttpRequestMessage source)
        {
            HttpRequestMessage copy = new(source.Method, source.RequestUri);
            foreach ((string name, IEnumerable<string> values) in source.Headers)
                copy.Headers.TryAddWithoutValidation(name, values);

            return copy;
        }

        public void Dispose()
        {
            Client.Dispose();
            _service.Shutdown();
            if (_thread.Join(TimeSpan.FromSeconds(2)) == false)
                throw new TimeoutException("PortalBridge service did not stop within two seconds.");

            CryptographicOperations.ZeroMemory(_key);
            File.Delete(_secretFile);
        }

        private static int GetFreePort(IPAddress address)
        {
            using TcpListener listener = new(address, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        private static void StopAttempt(PortalBridgeService service, Thread thread, byte[] key, string secretFile)
        {
            try
            {
                service?.Shutdown();
                if (thread != null && thread.Join(TimeSpan.FromSeconds(2)) == false)
                    throw new TimeoutException("PortalBridge service did not stop within two seconds.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                File.Delete(secretFile);
            }
        }

        private static void AddSignature(HttpRequestMessage request, byte[] key, string nonce)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string requestNonce = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            string rawUrl = request.RequestUri.PathAndQuery;
            string bodyDigest = request.Content == null
                ? EmptyBodyDigest
                : Convert.ToHexString(SHA256.HashData(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())).ToLowerInvariant();
            string canonical = string.Join('\n', request.Method.Method.ToUpperInvariant(), rawUrl,
                timestamp.ToString(CultureInfo.InvariantCulture), requestNonce, bodyDigest);
            string signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

            request.Headers.TryAddWithoutValidation(ContractVersionHeader, "1.0");
            request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(NonceHeader, requestNonce);
            request.Headers.TryAddWithoutValidation(BodyDigestHeader, bodyDigest);
            request.Headers.TryAddWithoutValidation(OperationIdHeader, Guid.NewGuid().ToString("D"));
            request.Headers.TryAddWithoutValidation(KeyIdHeader, "portal-primary");
            request.Headers.TryAddWithoutValidation(SignatureHeader, signature);
        }

        private sealed class SigningHandler : DelegatingHandler
        {
            private readonly byte[] _key;

            public SigningHandler(byte[] key) : base(new HttpClientHandler())
            {
                _key = key.ToArray();
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                AddSignature(request, _key, null);
                return base.SendAsync(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    CryptographicOperations.ZeroMemory(_key);

                base.Dispose(disposing);
            }
        }

    }
}
