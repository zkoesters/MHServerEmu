using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MHServerEmu.PortalBridge.Authentication
{
    public sealed class HmacRequestValidator : IDisposable
    {
        internal const string ContractVersionHeader = "X-Portal-Contract-Version";
        internal const string TimestampHeader = "X-Portal-Timestamp";
        internal const string NonceHeader = "X-Portal-Nonce";
        internal const string BodyDigestHeader = "X-Portal-Body-SHA256";
        internal const string OperationIdHeader = "X-Portal-Operation-Id";
        internal const string KeyIdHeader = "X-Portal-Key-Id";
        internal const string SignatureHeader = "X-Portal-Signature";

        private const string ContractVersion = "1.0";
        private const string EmptyBodyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan NonceRetention = TimeSpan.FromMinutes(5);

        private readonly object _lock = new();
        private readonly byte[] _key;
        private readonly string _keyId;
        private readonly INonceReplayCache _replayCache;
        private readonly TimeProvider _timeProvider;
        private bool _disposed;

        public HmacRequestValidator(ReadOnlySpan<byte> key, string keyId, INonceReplayCache replayCache,
            TimeProvider timeProvider)
        {
            if (key.Length != 32)
                throw new ArgumentException("PortalBridge HMAC key must contain exactly 32 bytes.", nameof(key));
            if (string.IsNullOrWhiteSpace(keyId))
                throw new ArgumentException("PortalBridge key ID is required.", nameof(keyId));

            _key = key.ToArray();
            _keyId = keyId;
            _replayCache = replayCache ?? throw new ArgumentNullException(nameof(replayCache));
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        public bool TryValidate(PortalBridgeRequest request, out Guid correlationId)
        {
            correlationId = Guid.NewGuid();
            lock (_lock)
            {
                if (_disposed)
                    return false;
                if (request == null || request.Method != "GET" || string.IsNullOrEmpty(request.RawUrl))
                    return false;
                if (TryReadSingleHeaders(request, out HeaderValues headers) == false)
                    return false;
                if (headers.ContractVersion != ContractVersion || headers.KeyId != _keyId)
                    return false;
                if (IsLowerHex(headers.Nonce, 32) == false || IsLowerHex(headers.BodyDigest, 64) == false ||
                    IsLowerHex(headers.Signature, 64) == false)
                    return false;
                if (request.HasEntityBody || request.ContentLength64 > 0 ||
                    string.IsNullOrWhiteSpace(request.TransferEncoding) == false)
                    return false;
                if (headers.BodyDigest != EmptyBodyDigest)
                    return false;
                if (long.TryParse(headers.Timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp) == false)
                    return false;

                long now = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
                if (timestamp < now - (long)ClockSkew.TotalSeconds || timestamp > now + (long)ClockSkew.TotalSeconds)
                    return false;

                string canonical = string.Join('\n', request.Method, request.RawUrl, headers.Timestamp, headers.Nonce,
                    headers.BodyDigest);
                byte[] expected = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical));
                byte[] supplied = Convert.FromHexString(headers.Signature);
                if (CryptographicOperations.FixedTimeEquals(expected, supplied) == false)
                    return false;

                return _replayCache.TryReserve(headers.Nonce, _timeProvider.GetUtcNow(), NonceRetention);
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                CryptographicOperations.ZeroMemory(_key);
            }
        }

        private static bool TryReadSingleHeaders(PortalBridgeRequest request, out HeaderValues headers)
        {
            headers = default;
            if (request == null)
                return false;

            if (TryGetSingleValue(request, ContractVersionHeader, out string contractVersion) == false ||
                TryGetSingleValue(request, TimestampHeader, out string timestamp) == false ||
                TryGetSingleValue(request, NonceHeader, out string nonce) == false ||
                TryGetSingleValue(request, BodyDigestHeader, out string bodyDigest) == false ||
                TryGetSingleValue(request, OperationIdHeader, out string operationId) == false ||
                TryGetSingleValue(request, KeyIdHeader, out string keyId) == false ||
                TryGetSingleValue(request, SignatureHeader, out string signature) == false)
                return false;

            if (Guid.TryParseExact(operationId, "D", out _) == false)
                return false;

            headers = new(contractVersion, timestamp, nonce, bodyDigest, keyId, signature);
            return true;
        }

        private static bool TryGetSingleValue(PortalBridgeRequest request, string name, out string value)
        {
            value = null;
            if (request.TryGetHeaderValues(name, out string[] values) == false || values?.Length != 1)
                return false;

            value = values[0];
            return value != null;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value?.Length != length)
                return false;

            return value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        }

        private readonly struct HeaderValues
        {
            public readonly string ContractVersion;
            public readonly string Timestamp;
            public readonly string Nonce;
            public readonly string BodyDigest;
            public readonly string KeyId;
            public readonly string Signature;

            public HeaderValues(string contractVersion, string timestamp, string nonce, string bodyDigest, string keyId,
                string signature)
            {
                ContractVersion = contractVersion;
                Timestamp = timestamp;
                Nonce = nonce;
                BodyDigest = bodyDigest;
                KeyId = keyId;
                Signature = signature;
            }
        }
    }
}
