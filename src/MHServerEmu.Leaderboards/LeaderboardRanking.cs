namespace MHServerEmu.Leaderboards
{
    public static class LeaderboardRanking
    {
        public static int GetCompetitionRank(int entryIndex, ulong score, bool hasPrevious, ulong previousScore, int previousRank)
        {
            int rank = entryIndex + 1;
            return hasPrevious && score == previousScore ? previousRank : rank;
        }

        public static int CompareEntries(ulong leftScore, ulong leftParticipantId, ulong rightScore, ulong rightParticipantId, bool ascending)
        {
            int scoreComparison = leftScore.CompareTo(rightScore);
            if (scoreComparison != 0)
                return ascending ? scoreComparison : -scoreComparison;

            return leftParticipantId.CompareTo(rightParticipantId);
        }
    }
}
