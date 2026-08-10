using System.Net;
using System.Security.Cryptography;
using System.Text;

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

            if (IsValidAddress(address) == false)
                return Fail("PortalBridge address is invalid.", out error);

            if (port < 1 || port > 65535)
                return Fail("PortalBridge port is invalid.", out error);

            if (string.IsNullOrWhiteSpace(keyId) || keyId.Length > 128)
                return Fail("PortalBridge key ID is invalid.", out error);

            if (Guid.TryParseExact(serverInstanceId, "D", out Guid instanceId) == false || instanceId == Guid.Empty)
                return Fail("PortalBridge server instance ID is invalid.", out error);

            byte[] rawSecret = null;
            byte[] encodedSecret = null;
            char[] encodedCharacters = null;
            byte[] decodedSecret = null;
            try
            {
                rawSecret = File.ReadAllBytes(secretFile);
                encodedSecret = CopyWithoutWhitespace(rawSecret);
                encodedCharacters = new char[Encoding.UTF8.GetCharCount(encodedSecret)];
                Encoding.UTF8.GetChars(encodedSecret.AsSpan(), encodedCharacters.AsSpan());
                decodedSecret = new byte[32];

                if (Convert.TryFromBase64Chars(encodedCharacters.AsSpan(), decodedSecret.AsSpan(), out int bytesWritten) == false ||
                    bytesWritten != decodedSecret.Length)
                    return Fail("PortalBridge secret must decode to exactly 32 bytes.", out error);

                settings = new(address, port, keyId, decodedSecret, instanceId);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is ArgumentException)
            {
                return Fail("PortalBridge secret file is invalid.", out error);
            }
            finally
            {
                if (rawSecret != null)
                    CryptographicOperations.ZeroMemory(rawSecret);
                if (encodedSecret != null)
                    CryptographicOperations.ZeroMemory(encodedSecret);
                if (encodedCharacters != null)
                    encodedCharacters.AsSpan().Clear();
                if (decodedSecret != null)
                    CryptographicOperations.ZeroMemory(decodedSecret);
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

        private static bool IsValidAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address) || address.Any(char.IsWhiteSpace) || address.Any(char.IsControl) ||
                address.Contains('/') || address.Contains('\\'))
                return false;

            if (address is "*" or "+")
                return true;

            if (IPAddress.TryParse(address, out IPAddress ipAddress))
                return ipAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IsCanonicalIPv4(address);

            if (address.Length > 253 || address.Contains(':'))
                return false;

            foreach (string label in address.Split('.'))
            {
                if (label.Length is < 1 or > 63 ||
                    char.IsAsciiLetterOrDigit(label[0]) == false ||
                    char.IsAsciiLetterOrDigit(label[^1]) == false)
                    return false;

                if (label.Any(character => char.IsAsciiLetterOrDigit(character) == false && character != '-'))
                    return false;
            }

            return true;
        }

        private static bool IsCanonicalIPv4(string address)
        {
            string[] octets = address.Split('.');
            if (octets.Length != 4)
                return false;

            foreach (string octet in octets)
            {
                if (octet.Length is < 1 or > 3 || (octet.Length > 1 && octet[0] == '0'))
                    return false;

                int value = 0;
                foreach (char character in octet)
                {
                    if (character is < '0' or > '9')
                        return false;

                    value = value * 10 + character - '0';
                }

                if (value > 255)
                    return false;
            }

            return true;
        }

        private static byte[] CopyWithoutWhitespace(ReadOnlySpan<byte> source)
        {
            int length = 0;
            foreach (byte value in source)
            {
                if (IsBase64Whitespace(value) == false)
                    length++;
            }

            byte[] copy = new byte[length];
            int index = 0;
            foreach (byte value in source)
            {
                if (IsBase64Whitespace(value) == false)
                    copy[index++] = value;
            }

            return copy;
        }

        private static bool IsBase64Whitespace(byte value)
        {
            return value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
        }
    }
}
