using System.Reflection;
using MHServerEmu.PortalBridge.Authentication;

namespace MHServerEmu.PortalBridge.Tests.Authentication
{
    public class HmacRequestValidatorTests
    {
        [Fact]
        public void PortalBridgeRequest_ConstructorCopiesHeaderArrays()
        {
            Dictionary<string, string[]> headers = TestRequestFactory.CreateSignedHeaders("GET", "/portal-bridge/v1/health",
                TestRequestFactory.FixedNow.ToUnixTimeSeconds(), "00112233445566778899aabbccddeeff", Guid.NewGuid());
            PortalBridgeRequest request = new("GET", "/portal-bridge/v1/health", false, 0, null, headers);
            headers[HmacRequestValidator.NonceHeader][0] = "changed";
            headers[HmacRequestValidator.TimestampHeader] = new[] { "changed" };

            Assert.Equal("00112233445566778899aabbccddeeff", request.Headers[HmacRequestValidator.NonceHeader][0]);
            Assert.Equal(TestRequestFactory.FixedNow.ToUnixTimeSeconds().ToString(), request.Headers[HmacRequestValidator.TimestampHeader][0]);
        }

        [Fact]
        public void TryValidate_ValidRequest_AcceptsAndReturnsOperationId()
        {
            Guid operationId = Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
            PortalBridgeRequest request = TestRequestFactory.Create(operationId: operationId);
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            bool result = validator.TryValidate(request, out Guid correlationId);

            Assert.True(result);
            Assert.Equal(operationId, correlationId);
        }

        [Theory]
        [InlineData(-60, true)]
        [InlineData(60, true)]
        [InlineData(-61, false)]
        [InlineData(61, false)]
        public void TryValidate_TimestampOffset_EnforcesInclusiveClockWindow(int seconds, bool expected)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(timestampOffsetSeconds: seconds);
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.Equal(expected, validator.TryValidate(request, out _));
        }

        [Theory]
        [MemberData(nameof(RequiredHeaders))]
        public void TryValidate_MissingRequiredHeader_Rejects(string header)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(mutateHeaders: headers => headers.Remove(header));
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Theory]
        [MemberData(nameof(RequiredHeaders))]
        public void TryValidate_DuplicateRequiredHeader_Rejects(string header)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(mutateHeaders: headers =>
                headers[header] = new[] { headers[header][0], headers[header][0] });
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Theory]
        [InlineData(HmacRequestValidator.NonceHeader)]
        [InlineData(HmacRequestValidator.BodyDigestHeader)]
        [InlineData(HmacRequestValidator.SignatureHeader)]
        public void TryValidate_UppercaseHex_Rejects(string header)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(mutateHeaders: headers =>
                headers[header] = new[] { headers[header][0].ToUpperInvariant() });
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Theory]
        [InlineData(HmacRequestValidator.ContractVersionHeader, "1.1")]
        [InlineData(HmacRequestValidator.KeyIdHeader, "other-key")]
        [InlineData(HmacRequestValidator.BodyDigestHeader, "0000000000000000000000000000000000000000000000000000000000000000")]
        [InlineData(HmacRequestValidator.SignatureHeader, "0000000000000000000000000000000000000000000000000000000000000000")]
        [InlineData(HmacRequestValidator.TimestampHeader, "not-a-timestamp")]
        [InlineData(HmacRequestValidator.NonceHeader, "not-hex")]
        public void TryValidate_TamperedHeader_Rejects(string header, string value)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(mutateHeaders: headers => headers[header] = new[] { value });
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Theory]
        [InlineData("POST", "/portal-bridge/v1/health")]
        [InlineData("GET", "/portal-bridge/v1/capabilities?extra=true")]
        public void TryValidate_TamperedCanonicalRequest_Rejects(string method, string rawUrl)
        {
            Dictionary<string, string[]> headers = TestRequestFactory.CreateSignedHeaders("GET", "/portal-bridge/v1/health",
                TestRequestFactory.FixedNow.ToUnixTimeSeconds(), "00112233445566778899aabbccddeeff",
                Guid.Parse("4b56bb3d-8b6e-4be4-a754-2f99ab40f26a"));
            PortalBridgeRequest request = new(method, rawUrl, false, 0, null, headers);
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Fact]
        public void TryValidate_EntityBody_Rejects()
        {
            PortalBridgeRequest request = TestRequestFactory.Create(hasEntityBody: true);
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Fact]
        public void TryValidate_ContentLength_Rejects()
        {
            PortalBridgeRequest request = TestRequestFactory.Create(contentLength: 1);
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Fact]
        public void TryValidate_ChunkedTransferEncoding_Rejects()
        {
            PortalBridgeRequest request = TestRequestFactory.Create(transferEncoding: "chunked");
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not-a-uuid")]
        public void TryValidate_InvalidOperationId_ReturnsGeneratedCorrelationIdAndRejects(string operationId)
        {
            PortalBridgeRequest request = TestRequestFactory.Create(mutateHeaders: headers =>
            {
                if (operationId == null)
                    headers.Remove(HmacRequestValidator.OperationIdHeader);
                else
                    headers[HmacRequestValidator.OperationIdHeader] = new[] { operationId };
            });
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator();

            Assert.False(validator.TryValidate(request, out Guid correlationId));
            Assert.NotEqual(Guid.Empty, correlationId);
        }

        [Fact]
        public void TryValidate_ReplayedRequest_RejectsSecondValidation()
        {
            InMemoryNonceReplayCache cache = new();
            PortalBridgeRequest request = TestRequestFactory.Create();
            using HmacRequestValidator validator = TestRequestFactory.CreateValidator(cache);

            Assert.True(validator.TryValidate(request, out _));
            Assert.False(validator.TryValidate(request, out _));
        }

        [Fact]
        public void Constructor_ClonesKeyAndDispose_ZeroesOwnedKey()
        {
            byte[] key = TestRequestFactory.Key.ToArray();
            using HmacRequestValidator validator = new(key, "portal-primary", new InMemoryNonceReplayCache(), TestTimeProvider.Instance);
            Array.Fill(key, (byte)0);

            Assert.True(validator.TryValidate(TestRequestFactory.Create(), out _));
            validator.Dispose();

            byte[] ownedKey = (byte[])typeof(HmacRequestValidator)
                .GetField("_key", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(validator);
            Assert.Equal(new byte[32], ownedKey);
        }

        public static IEnumerable<object[]> RequiredHeaders()
        {
            yield return new object[] { HmacRequestValidator.ContractVersionHeader };
            yield return new object[] { HmacRequestValidator.TimestampHeader };
            yield return new object[] { HmacRequestValidator.NonceHeader };
            yield return new object[] { HmacRequestValidator.BodyDigestHeader };
            yield return new object[] { HmacRequestValidator.OperationIdHeader };
            yield return new object[] { HmacRequestValidator.KeyIdHeader };
            yield return new object[] { HmacRequestValidator.SignatureHeader };
        }

        private sealed class TestTimeProvider : TimeProvider
        {
            public static readonly TestTimeProvider Instance = new();

            public override DateTimeOffset GetUtcNow()
            {
                return TestRequestFactory.FixedNow;
            }
        }
    }
}
