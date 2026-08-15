using MHServerEmu.DatabaseAccess.Persistence;

namespace MHServerEmu.DatabaseAccess.Tests.Persistence
{
    public class PersistenceProviderSelectorTests
    {
        [Theory]
        [InlineData("Json", PersistenceProvider.Json)]
        [InlineData("json", PersistenceProvider.Json)]
        [InlineData("SQLite", PersistenceProvider.SQLite)]
        [InlineData("PostgreSQL", PersistenceProvider.PostgreSQL)]
        public void TrySelect_KnownProvider_SelectsProvider(string configuredProvider, PersistenceProvider expectedProvider)
        {
            bool selected = PersistenceProviderSelector.TrySelect(configuredProvider, false, out PersistenceProvider provider, out string error);

            Assert.True(selected);
            Assert.Equal(expectedProvider, provider);
            Assert.Empty(error);
        }

        [Theory]
        [InlineData(true, PersistenceProvider.Json)]
        [InlineData(false, PersistenceProvider.SQLite)]
        public void TrySelect_EmptyProvider_SelectsDefaultForLegacyFlag(bool useLegacyJson, PersistenceProvider expectedProvider)
        {
            bool selected = PersistenceProviderSelector.TrySelect(string.Empty, useLegacyJson, out PersistenceProvider provider, out string error);

            Assert.True(selected);
            Assert.Equal(expectedProvider, provider);
            Assert.Empty(error);
        }

        [Fact]
        public void TrySelect_ConfiguredProviderWithLegacyFlag_ReturnsConflictError()
        {
            bool selected = PersistenceProviderSelector.TrySelect("SQLite", true, out PersistenceProvider provider, out string error);

            Assert.False(selected);
            Assert.Equal(default, provider);
            Assert.NotEmpty(error);
        }

        [Theory]
        [InlineData("Unknown")]
        [InlineData("3")]
        [InlineData("-1")]
        public void TrySelect_UnknownOrNumericProvider_ReturnsError(string configuredProvider)
        {
            bool selected = PersistenceProviderSelector.TrySelect(configuredProvider, false, out PersistenceProvider provider, out string error);

            Assert.False(selected);
            Assert.Equal(default, provider);
            Assert.NotEmpty(error);
        }
    }
}
