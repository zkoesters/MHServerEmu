using MHServerEmu.DatabaseAccess.Persistence;

namespace MHServerEmu.DatabaseAccess.Tests.Persistence
{
    public class PersistenceCapabilitiesTests
    {
        [Fact]
        public void Presets_HaveExpectedValuesAndNoPublicSetters()
        {
            AssertCapabilities(PersistenceCapabilities.Json, false, false, false);
            AssertCapabilities(PersistenceCapabilities.SQLite, true, true, false);
            AssertCapabilities(PersistenceCapabilities.PostgreSQL, true, true, true);

            Assert.Null(typeof(PersistenceCapabilities).GetProperty(nameof(PersistenceCapabilities.VerifyAccountCredentials)).GetSetMethod());
            Assert.Null(typeof(PersistenceCapabilities).GetProperty(nameof(PersistenceCapabilities.VerifyClientTokens)).GetSetMethod());
            Assert.Null(typeof(PersistenceCapabilities).GetProperty(nameof(PersistenceCapabilities.HasDurableSecurityVersions)).GetSetMethod());
        }

        private static void AssertCapabilities(PersistenceCapabilities capabilities, bool verifyAccountCredentials, bool verifyClientTokens, bool hasDurableSecurityVersions)
        {
            Assert.Equal(verifyAccountCredentials, capabilities.VerifyAccountCredentials);
            Assert.Equal(verifyClientTokens, capabilities.VerifyClientTokens);
            Assert.Equal(hasDurableSecurityVersions, capabilities.HasDurableSecurityVersions);
        }
    }
}
