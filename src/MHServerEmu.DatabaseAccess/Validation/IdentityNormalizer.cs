using System.Text;

namespace MHServerEmu.DatabaseAccess.Validation
{
    public static class IdentityNormalizer
    {
        public static string NormalizeEmail(string email)
        {
            return NormalizeText(email, nameof(email)).ToLowerInvariant();
        }

        public static string NormalizeGuildName(string guildName)
        {
            return NormalizeText(guildName, nameof(guildName)).ToUpperInvariant();
        }

        public static string NormalizePlayerName(string playerName)
        {
            if (string.IsNullOrEmpty(playerName) || playerName.Length > 16)
                throw new ArgumentException("Player name must contain 1 to 16 ASCII alphanumeric characters.", nameof(playerName));

            foreach (char character in playerName)
            {
                if ((character is < 'A' or > 'Z') && (character is < 'a' or > 'z') && (character is < '0' or > '9'))
                    throw new ArgumentException("Player name must contain 1 to 16 ASCII alphanumeric characters.", nameof(playerName));
            }

            return playerName.ToUpperInvariant();
        }

        private static string NormalizeText(string value, string parameterName)
        {
            if (value == null)
                throw new ArgumentException("Identity value cannot be null.", parameterName);

            string normalized = value.Trim().Normalize(NormalizationForm.FormC);
            if (normalized.Length > 320)
                throw new ArgumentException("Identity value cannot exceed 320 characters.", parameterName);

            foreach (char character in normalized)
            {
                if (char.IsControl(character))
                    throw new ArgumentException("Identity value cannot contain control characters.", parameterName);
            }

            return normalized;
        }
    }
}
