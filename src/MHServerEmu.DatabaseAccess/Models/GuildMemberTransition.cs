namespace MHServerEmu.DatabaseAccess.Models
{
    public sealed class GuildMemberTransition
    {
        public long GuildId { get; }
        public long ExpectedRevision { get; }
        public IReadOnlyList<GuildMemberChange> Changes { get; }

        public GuildMemberTransition(long guildId, long expectedRevision, params GuildMemberChange[] changes)
        {
            ArgumentNullException.ThrowIfNull(changes);

            if (changes.Length is < 1 or > 2)
                throw new ArgumentException("A transition must contain one or two member changes.", nameof(changes));

            HashSet<long> playerDbGuids = new();
            foreach (GuildMemberChange change in changes)
            {
                if (playerDbGuids.Add(change.PlayerDbGuid) == false)
                    throw new ArgumentException("A transition cannot contain multiple changes for the same player.", nameof(changes));

                ValidateMembership(change.ExpectedMembership, nameof(changes));
                ValidateMembership(change.NewMembership, nameof(changes));
            }

            GuildId = guildId;
            ExpectedRevision = expectedRevision;
            Changes = Array.AsReadOnly(changes.ToArray());
        }

        private static void ValidateMembership(long? membership, string parameterName)
        {
            if (membership is < 1 or > 3)
                throw new ArgumentOutOfRangeException(parameterName, "Guild membership must be between 1 and 3.");
        }
    }
}
