using MHServerEmu.PortalBridge.Authentication;

namespace MHServerEmu.PortalBridge.Tests.Authentication
{
    public sealed class OpaqueAccountIdGeneratorTests
    {
        [Fact]
        public void GetAccountId_SameSecretAndAccount_ReturnsStableOpaqueId()
        {
            byte[] secret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            using OpaqueAccountIdGenerator generator = new(secret);

            string first = generator.GetAccountId(0x2000000000000001);
            string second = generator.GetAccountId(0x2000000000000001);

            Assert.Equal(first, second);
            Assert.StartsWith("acct_", first, StringComparison.Ordinal);
            Assert.DoesNotContain("2000000000000001", first, StringComparison.Ordinal);
        }

        [Fact]
        public void GetAccountId_DifferentAccountOrSecret_ReturnsDifferentOpaqueIds()
        {
            byte[] firstSecret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            byte[] secondSecret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            using OpaqueAccountIdGenerator firstGenerator = new(firstSecret);
            using OpaqueAccountIdGenerator secondGenerator = new(secondSecret);

            string accountId = firstGenerator.GetAccountId(0x2000000000000001);

            Assert.NotEqual(accountId, firstGenerator.GetAccountId(0x2000000000000002));
            Assert.NotEqual(accountId, secondGenerator.GetAccountId(0x2000000000000001));
        }
    }
}
