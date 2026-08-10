namespace MHServerEmu.PortalBridge.Tests
{
    public class PortalBridgeConfigTests
    {
        [Fact]
        public void DefaultConfiguration_UsesDisabledIniDefaults()
        {
            PortalBridgeConfig config = new();

            Assert.False(config.Enabled);
            Assert.Equal("localhost", config.Address);
            Assert.Equal(8090, config.Port);
            Assert.Equal("portal-primary", config.KeyId);
            Assert.Equal(string.Empty, config.SecretFile);
            Assert.Equal(string.Empty, config.ServerInstanceId);
        }

        [Fact]
        public void TryCreateSettings_ValidConfiguration_DecodesExactKeyAndInstanceId()
        {
            byte[] expectedSecret = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
            string secretFile = Path.GetTempFileName();
            File.WriteAllText(secretFile, Convert.ToBase64String(expectedSecret));
            PortalBridgeConfig config = new()
            {
                Enabled = true,
                Address = "*",
                Port = 8090,
                KeyId = "portal-primary",
                SecretFile = secretFile,
                ServerInstanceId = "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a",
            };

            try
            {
                Assert.True(config.TryCreateSettings(out PortalBridgeSettings settings, out string error), error);
                using (settings)
                {
                    Assert.Equal(expectedSecret, settings.Secret.ToArray());
                    Assert.Equal(Guid.Parse(config.ServerInstanceId), settings.ServerInstanceId);
                }
            }
            finally
            {
                File.Delete(secretFile);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("bad/address")]
        [InlineData("bad\\address")]
        [InlineData("bad\u0001address")]
        public void TryCreateSettings_InvalidAddress_FailsWithoutSecretContents(string address)
        {
            AssertInvalidConfiguration(address, 8090, "portal-primary", "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65536)]
        public void TryCreateSettings_InvalidPort_FailsWithoutSecretContents(int port)
        {
            AssertInvalidConfiguration("*", port, "portal-primary", "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
        }

        [Theory]
        [InlineData("")]
        [InlineData("                                                                                                                                 ")]
        public void TryCreateSettings_InvalidKeyId_FailsWithoutSecretContents(string keyId)
        {
            AssertInvalidConfiguration("*", 8090, keyId, "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a");
        }

        [Theory]
        [InlineData("")]
        [InlineData("4b56bb3d-8b6e-4be4-a754-2f99ab40f26aX")]
        [InlineData("00000000-0000-0000-0000-000000000000")]
        public void TryCreateSettings_InvalidServerInstanceId_FailsWithoutSecretContents(string serverInstanceId)
        {
            AssertInvalidConfiguration("*", 8090, "portal-primary", serverInstanceId);
        }

        [Theory]
        [InlineData("not base64")]
        [InlineData("AQID")]
        public void TryCreateSettings_InvalidSecret_FailsWithoutSecretContents(string secretContents)
        {
            string secretFile = Path.GetTempFileName();
            File.WriteAllText(secretFile, secretContents);
            PortalBridgeConfig config = new()
            {
                Enabled = true,
                Address = "*",
                Port = 8090,
                KeyId = "portal-primary",
                SecretFile = secretFile,
                ServerInstanceId = "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a",
            };

            try
            {
                Assert.False(config.TryCreateSettings(out _, out string error));
                Assert.DoesNotContain(secretContents, error, StringComparison.Ordinal);
            }
            finally
            {
                File.Delete(secretFile);
            }
        }

        [Fact]
        public void Dispose_ZeroesOwnedSecret()
        {
            string secretFile = Path.GetTempFileName();
            File.WriteAllText(secretFile, Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
            PortalBridgeConfig config = new()
            {
                Enabled = true,
                Address = "*",
                Port = 8090,
                KeyId = "portal-primary",
                SecretFile = secretFile,
                ServerInstanceId = "4b56bb3d-8b6e-4be4-a754-2f99ab40f26a",
            };

            try
            {
                Assert.True(config.TryCreateSettings(out PortalBridgeSettings settings, out string error), error);
                settings.Dispose();

                Assert.Equal(new byte[32], settings.Secret.ToArray());
            }
            finally
            {
                File.Delete(secretFile);
            }
        }

        private static void AssertInvalidConfiguration(string address, int port, string keyId, string serverInstanceId)
        {
            const string secretContents = "not-a-secret";
            PortalBridgeConfig config = new()
            {
                Enabled = true,
                Address = address,
                Port = port,
                KeyId = keyId,
                SecretFile = secretContents,
                ServerInstanceId = serverInstanceId,
            };

            Assert.False(config.TryCreateSettings(out _, out string error));
            Assert.DoesNotContain(secretContents, error, StringComparison.Ordinal);
        }
    }
}
