using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MHServerEmu.PortalBridge.Authentication
{
    internal sealed class OpaqueAccountIdGenerator : IDisposable
    {
        private const string Domain = "MHServerEmu.PortalBridge.OpaqueAccountId.v1:";

        private readonly byte[] _key;
        private bool _disposed;

        public OpaqueAccountIdGenerator(ReadOnlySpan<byte> key)
        {
            if (key.Length != 32)
                throw new ArgumentException("PortalBridge HMAC key must contain exactly 32 bytes.", nameof(key));

            _key = key.ToArray();
        }

        public string GetAccountId(long accountId)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte[] message = Encoding.UTF8.GetBytes(Domain + accountId.ToString(CultureInfo.InvariantCulture));
            try
            {
                byte[] digest = HMACSHA256.HashData(_key, message);
                return "acct_" + Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
