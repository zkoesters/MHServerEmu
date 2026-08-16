using MHServerEmu.DatabaseAccess.Validation;

namespace MHServerEmu.DatabaseAccess.Tests.Capabilities
{
    public class IdentityNormalizerTests
    {
        [Fact]
        public void NormalizeEmail_TrimsNormalizesAndLowercasesInvariantly()
        {
            string normalized = IdentityNormalizer.NormalizeEmail("  A\u030A@EXAMPLE.COM  ");

            Assert.Equal("\u00E5@example.com", normalized);
        }

        [Fact]
        public void NormalizeGuildName_TrimsNormalizesAndUppercasesInvariantly()
        {
            string normalized = IdentityNormalizer.NormalizeGuildName("  a\u030A guild  ");

            Assert.Equal("\u00C5 GUILD", normalized);
        }

        [Fact]
        public void NormalizePlayerName_UppercasesAsciiAlphanumericName()
        {
            string normalized = IdentityNormalizer.NormalizePlayerName("player42");

            Assert.Equal("PLAYER42", normalized);
        }

        [Theory]
        [InlineData("name\n@example.com")]
        [InlineData("name\u007F@example.com")]
        public void NormalizeEmail_RejectsControlCharacters(string email)
        {
            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizeEmail(email));
        }

        [Theory]
        [InlineData("\nname@example.com")]
        [InlineData("name@example.com\t")]
        public void NormalizeEmail_RejectsLeadingAndTrailingControlCharacters(string email)
        {
            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizeEmail(email));
        }

        [Theory]
        [InlineData("\nguild")]
        [InlineData("guild\t")]
        public void NormalizeGuildName_RejectsLeadingAndTrailingControlCharacters(string guildName)
        {
            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizeGuildName(guildName));
        }

        [Fact]
        public void NormalizeGuildName_RejectsValuesOver320CharactersAfterNormalization()
        {
            string guildName = new('a', 321);

            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizeGuildName(guildName));
        }

        [Fact]
        public void NormalizeEmail_RejectsValuesOver320CharactersAfterNormalization()
        {
            string email = new('a', 321);

            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizeEmail(email));
        }

        [Theory]
        [InlineData("")]
        [InlineData("player-name")]
        [InlineData("player name")]
        [InlineData("player!")]
        [InlineData("\u00E5")]
        [InlineData("abcdefghijklmnopq")]
        public void NormalizePlayerName_RejectsNonAsciiAlphanumericOrInvalidLength(string playerName)
        {
            Assert.Throws<ArgumentException>(() => IdentityNormalizer.NormalizePlayerName(playerName));
        }
    }
}
