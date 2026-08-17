namespace MHServerEmu.Leaderboards
{
    public static class LeaderboardRanking
    {
        public static int GetCompetitionRank(int entryIndex, ulong score, bool hasPrevious, ulong previousScore, int previousRank)
        {
            int rank = entryIndex + 1;
            return hasPrevious && score == previousScore ? previousRank : rank;
        }
    }
}
