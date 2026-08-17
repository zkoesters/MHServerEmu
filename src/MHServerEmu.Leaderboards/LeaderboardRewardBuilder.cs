using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.Leaderboards
{
    public static class LeaderboardRewardBuilder
    {
        public static bool TryBuild(IEnumerable<DBRewardEntry> candidates, out IReadOnlyList<DBRewardEntry> rewards)
        {
            if (candidates == null)
                throw new ArgumentNullException(nameof(candidates));

            List<DBRewardEntry> result = new();
            HashSet<long> participants = new();
            foreach (DBRewardEntry reward in candidates)
            {
                if (reward == null || participants.Add(reward.ParticipantId) == false)
                {
                    rewards = Array.Empty<DBRewardEntry>();
                    return false;
                }

                result.Add(reward);
            }

            rewards = result;
            return true;
        }
    }
}
