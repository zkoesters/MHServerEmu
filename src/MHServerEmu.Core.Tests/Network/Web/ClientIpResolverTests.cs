using System.Net;
using MHServerEmu.Core.Network.Web;

namespace MHServerEmu.Core.Tests.Network.Web
{
    public class ClientIpResolverTests
    {
        [Fact]
        public void Resolve_IgnoresForwardedHeaderFromUntrustedPeer()
        {
            ClientIpResult result = ClientIpResolver.Resolve(IPAddress.Parse("203.0.113.10"), "198.51.100.20", [IpNetwork.Parse("10.0.0.0/8")]);

            Assert.Equal(IPAddress.Parse("203.0.113.10"), result.Address);
            Assert.False(result.IsForwarded);
        }

        [Fact]
        public void Resolve_ReturnsFirstUntrustedAddressFromTrustedChain()
        {
            ClientIpResult result = ClientIpResolver.Resolve(IPAddress.Parse("10.0.0.2"), "198.51.100.20, 10.0.0.1", [IpNetwork.Parse("10.0.0.0/8")]);

            Assert.Equal(IPAddress.Parse("198.51.100.20"), result.Address);
            Assert.True(result.IsForwarded);
        }

        [Theory]
        [InlineData("198.51.100.20, not-an-ip")]
        [InlineData(",")]
        [InlineData("198.51.100.20, 10.0.0.1", 1)]
        public void Resolve_FallsBackToPeerForMalformedOrExcessiveForwardedValues(string forwardedFor, int maxHops = 16)
        {
            IPAddress peer = IPAddress.Parse("10.0.0.2");

            ClientIpResult result = ClientIpResolver.Resolve(peer, forwardedFor, [IpNetwork.Parse("10.0.0.0/8")], maxHops: maxHops);

            Assert.Equal(peer, result.Address);
            Assert.False(result.IsForwarded);
        }

        [Fact]
        public void Resolve_FallsBackToPeerForOverlongForwardedHeader()
        {
            IPAddress peer = IPAddress.Parse("10.0.0.2");

            ClientIpResult result = ClientIpResolver.Resolve(peer, "198.51.100.20", [IpNetwork.Parse("10.0.0.0/8")], maxHeaderLength: 12);

            Assert.Equal(peer, result.Address);
            Assert.False(result.IsForwarded);
        }

        [Fact]
        public void Resolve_ReturnsLeftmostAddressWhenAllForwardedHopsAreTrusted()
        {
            ClientIpResult result = ClientIpResolver.Resolve(IPAddress.Parse("10.0.0.3"), "10.0.0.1, 10.0.0.2", [IpNetwork.Parse("10.0.0.0/8")]);

            Assert.Equal(IPAddress.Parse("10.0.0.1"), result.Address);
            Assert.True(result.IsForwarded);
        }

        [Fact]
        public void Resolve_NormalizesIpv4MappedIpv6Addresses()
        {
            ClientIpResult result = ClientIpResolver.Resolve(IPAddress.Parse("::ffff:10.0.0.2"), "::ffff:198.51.100.20", [IpNetwork.Parse("10.0.0.0/8")]);

            Assert.Equal(IPAddress.Parse("198.51.100.20"), result.Address);
            Assert.Equal(global::System.Net.Sockets.AddressFamily.InterNetwork, result.Address.AddressFamily);
            Assert.True(result.IsForwarded);
            Assert.True(IpNetwork.Parse("10.0.0.0/8").Contains(IPAddress.Parse("::ffff:10.1.2.3")));
        }
    }
}
