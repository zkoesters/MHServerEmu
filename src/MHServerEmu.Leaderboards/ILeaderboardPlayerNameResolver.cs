namespace MHServerEmu.Leaderboards
{
    public interface ILeaderboardPlayerNameResolver
    {
        string GetPlayerName(ulong participantId);
    }
}
