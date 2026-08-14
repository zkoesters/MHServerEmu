using System.Net;

namespace MHServerEmu.Core.Network.Web
{
    public readonly record struct ClientIpResult(IPAddress Address, bool IsForwarded);

    public static class ClientIpResolver
    {
        public static ClientIpResult Resolve(IPAddress peer, string forwardedFor, IReadOnlyCollection<IpNetwork> trustedNetworks,
            int maxHeaderLength = 2048, int maxHops = 16)
        {
            peer = IpNetwork.Normalize(peer);
            if (IsTrusted(peer, trustedNetworks) == false || string.IsNullOrWhiteSpace(forwardedFor))
                return new(peer, false);

            if (maxHeaderLength <= 0 || maxHops <= 0 || forwardedFor.Length > maxHeaderLength)
                return new(peer, false);

            string[] values = forwardedFor.Split(',', StringSplitOptions.TrimEntries);
            if (values.Length == 0 || values.Length > maxHops)
                return new(peer, false);

            IPAddress[] addresses = new IPAddress[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                if (IPAddress.TryParse(values[i], out IPAddress address) == false)
                    return new(peer, false);

                addresses[i] = IpNetwork.Normalize(address);
            }

            for (int i = addresses.Length - 1; i >= 0; i--)
            {
                if (IsTrusted(addresses[i], trustedNetworks) == false)
                    return new(addresses[i], true);
            }

            return new(addresses[0], true);
        }

        private static bool IsTrusted(IPAddress address, IReadOnlyCollection<IpNetwork> trustedNetworks)
        {
            if (trustedNetworks == null)
                return false;

            foreach (IpNetwork network in trustedNetworks)
            {
                if (network.Contains(address))
                    return true;
            }

            return false;
        }
    }
}
