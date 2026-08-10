using System.Security.Cryptography;

namespace MHServerEmu.PortalBridge
{
    public sealed class PortalBridgeSettings : IDisposable
    {
        private readonly byte[] _secret;

        public string Address { get; }
        public int Port { get; }
        public string KeyId { get; }
        public Guid ServerInstanceId { get; }
        public ReadOnlySpan<byte> Secret { get => _secret; }

        private PortalBridgeSettings(string address, int port, string keyId, byte[] secret, Guid serverInstanceId)
        {
            Address = address;
            Port = port;
            KeyId = keyId;
            _secret = secret.ToArray();
            ServerInstanceId = serverInstanceId;
        }

        public static bool TryCreate(string address, int port, string keyId, string secretFile,
            string serverInstanceId, out PortalBridgeSettings settings, out string error)
        {
            settings = null;
            error = null;

            if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsControl) || address.Contains('/') || address.Contains('\\'))
                return Fail("PortalBridge address is invalid.", out error);

            if (port < 1 || port > 65535)
                return Fail("PortalBridge port is invalid.", out error);

            if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128)
                return Fail("PortalBridge key ID is invalid.", out error);

            if (Guid.TryParseExact(serverInstanceId, "D", out Guid instanceId) == false || instanceId == Guid.Empty)
                return Fail("PortalBridge server instance ID is invalid.", out error);

            byte[] secret = null;
            try
            {
                secret = Convert.FromBase64String(File.ReadAllText(secretFile));
                if (secret.Length != 32)
                    return Fail("PortalBridge secret must decode to exactly 32 bytes.", out error);

                settings = new(address, port, keyId, secret, instanceId);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is ArgumentException)
            {
                return Fail("PortalBridge secret file is invalid.", out error);
            }
            finally
            {
                if (secret != null)
                    CryptographicOperations.ZeroMemory(secret);
            }
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_secret);
        }

        private static bool Fail(string message, out string error)
        {
            error = message;
            return false;
        }
    }
}
