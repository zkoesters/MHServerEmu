using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MHServerEmu.PortalBridge.Authentication;

namespace MHServerEmu.PortalBridge.Tests
{
    internal static class TestRequestFactory
    {
        internal static readonly DateTimeOffset FixedNow = DateTimeOffset.FromUnixTimeSeconds(1710000000);
        internal static readonly byte[] Key = Convert.FromBase64String("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");

        internal static PortalBridgeRequest Create(Guid? operationId = null, int timestampOffsetSeconds = 0,
            string method = "GET", string rawUrl = "/portal-bridge/v1/health",
            Action<Dictionary<string, string[]>> mutateHeaders = null,
            bool hasEntityBody = false, long contentLength = 0, string transferEncoding = null)
        {
            Guid id = operationId ?? Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
            long timestamp = FixedNow.AddSeconds(timestampOffsetSeconds).ToUnixTimeSeconds();
            Dictionary<string, string[]> headers = CreateSignedHeaders(method, rawUrl, timestamp,
                "00112233445566778899aabbccddeeff", id);
            mutateHeaders?.Invoke(headers);
            return new PortalBridgeRequest(method, rawUrl, hasEntityBody, contentLength, transferEncoding, headers);
        }

        internal static HmacRequestValidator CreateValidator(INonceReplayCache replayCache = null)
        {
            return new HmacRequestValidator(Key, "portal-primary", replayCache ?? new InMemoryNonceReplayCache(),
                new FixedTimeProvider(FixedNow));
        }

        internal static Dictionary<string, string[]> CreateSignedHeaders(string method, string rawUrl,
            long timestamp, string nonce, Guid operationId)
        {
            const string digest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            string canonical = string.Join('\n', method.ToUpperInvariant(), rawUrl,
                timestamp.ToString(CultureInfo.InvariantCulture), nonce, digest);
            string signature = Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                [HmacRequestValidator.ContractVersionHeader] = new[] { "1.0" },
                [HmacRequestValidator.TimestampHeader] = new[] { timestamp.ToString(CultureInfo.InvariantCulture) },
                [HmacRequestValidator.NonceHeader] = new[] { nonce },
                [HmacRequestValidator.BodyDigestHeader] = new[] { digest },
                [HmacRequestValidator.OperationIdHeader] = new[] { operationId.ToString("D") },
                [HmacRequestValidator.KeyIdHeader] = new[] { "portal-primary" },
                [HmacRequestValidator.SignatureHeader] = new[] { signature },
            };
        }

        internal static HttpRequestMessage CreateSignedGetRequest(string listenUrl, string rawUrl, string nonce)
        {
            Guid operationId = Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
            Dictionary<string, string[]> headers = CreateSignedHeaders("GET", rawUrl, FixedNow.ToUnixTimeSeconds(),
                nonce, operationId);
            HttpRequestMessage request = new(HttpMethod.Get, new Uri(new Uri(listenUrl), rawUrl));

            foreach ((string name, string[] values) in headers)
                request.Headers.TryAddWithoutValidation(name, values);

            return request;
        }

        private sealed class FixedTimeProvider : TimeProvider
        {
            private readonly DateTimeOffset _now;

            public FixedTimeProvider(DateTimeOffset now)
            {
                _now = now;
            }

            public override DateTimeOffset GetUtcNow()
            {
                return _now;
            }
        }
    }
}
