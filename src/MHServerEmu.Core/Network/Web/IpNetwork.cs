using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace MHServerEmu.Core.Network.Web
{
    public sealed class IpNetwork
    {
        public IPAddress NetworkAddress { get; }
        public int PrefixLength { get; }

        private IpNetwork(IPAddress networkAddress, int prefixLength)
        {
            NetworkAddress = networkAddress;
            PrefixLength = prefixLength;
        }

        public static IpNetwork Parse(string cidr)
        {
            if (string.IsNullOrWhiteSpace(cidr) || cidr != cidr.Trim())
                throw new FormatException("CIDR network must contain an address and prefix length.");

            int separatorIndex = cidr.IndexOf('/');
            if (separatorIndex <= 0 || separatorIndex != cidr.LastIndexOf('/') || separatorIndex == cidr.Length - 1)
                throw new FormatException($"Invalid CIDR network '{cidr}'.");

            if (IPAddress.TryParse(cidr[..separatorIndex], out IPAddress address) == false ||
                int.TryParse(cidr[(separatorIndex + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int prefixLength) == false)
            {
                throw new FormatException($"Invalid CIDR network '{cidr}'.");
            }

            if (address.IsIPv4MappedToIPv6)
            {
                if (prefixLength < 96 || prefixLength > 128)
                    throw new FormatException($"Invalid IPv4-mapped CIDR prefix '{cidr}'.");

                address = address.MapToIPv4();
                prefixLength -= 96;
            }

            int addressBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (prefixLength < 0 || prefixLength > addressBits)
                throw new FormatException($"Invalid CIDR prefix '{cidr}'.");

            return new(MaskAddress(address, prefixLength), prefixLength);
        }

        public bool Contains(IPAddress address)
        {
            address = Normalize(address);
            if (address.AddressFamily != NetworkAddress.AddressFamily)
                return false;

            byte[] networkBytes = NetworkAddress.GetAddressBytes();
            byte[] addressBytes = address.GetAddressBytes();
            int fullBytes = PrefixLength / 8;
            int remainingBits = PrefixLength % 8;

            for (int i = 0; i < fullBytes; i++)
            {
                if (networkBytes[i] != addressBytes[i])
                    return false;
            }

            if (remainingBits == 0)
                return true;

            byte mask = (byte)(0xFF << (8 - remainingBits));
            return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
        }

        internal static IPAddress Normalize(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        private static IPAddress MaskAddress(IPAddress address, int prefixLength)
        {
            byte[] bytes = address.GetAddressBytes();
            int fullBytes = prefixLength / 8;
            int remainingBits = prefixLength % 8;

            if (fullBytes < bytes.Length && remainingBits != 0)
                bytes[fullBytes] &= (byte)(0xFF << (8 - remainingBits));

            for (int i = fullBytes + (remainingBits == 0 ? 0 : 1); i < bytes.Length; i++)
                bytes[i] = 0;

            return new(bytes);
        }
    }
}
