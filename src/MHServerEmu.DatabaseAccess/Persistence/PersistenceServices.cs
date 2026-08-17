namespace MHServerEmu.DatabaseAccess.Persistence
{
    public sealed class PersistenceServices
    {
        public IAccountStore Accounts { get; }
        public IPlayerStore Players { get; }
        public IGuildStore Guilds { get; }
        public ILeaderboardStore Leaderboards { get; }
        public PersistenceCapabilities Capabilities { get; }

        public PersistenceServices(IAccountStore accounts, IPlayerStore players, IGuildStore guilds, ILeaderboardStore leaderboards, PersistenceCapabilities capabilities)
        {
            Accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
            Players = players ?? throw new ArgumentNullException(nameof(players));
            Guilds = guilds ?? throw new ArgumentNullException(nameof(guilds));
            Leaderboards = leaderboards ?? throw new ArgumentNullException(nameof(leaderboards));
            Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        }
    }
}
