using Gazillion;

namespace MHServerEmu.DatabaseAccess.Models.Leaderboards
{
    public sealed record LeaderboardDefinitionSpec
    {
        public long LeaderboardId { get; }
        public string PrototypeName { get; }
        public bool IsEnabled { get; }
        public long StartTime { get; }
        public int MaxResetCount { get; }

        public LeaderboardDefinitionSpec(long leaderboardId, string prototypeName, bool isEnabled, long startTime, int maxResetCount)
        {
            LeaderboardId = leaderboardId;
            PrototypeName = prototypeName ?? throw new ArgumentNullException(nameof(prototypeName));
            IsEnabled = isEnabled;
            StartTime = startTime;
            MaxResetCount = maxResetCount;
        }

        public static LeaderboardDefinitionSpec From(DBLeaderboard definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            return new(definition.LeaderboardId, definition.PrototypeName, definition.IsEnabled, definition.StartTime, definition.MaxResetCount);
        }
    }

    public sealed record LeaderboardInstanceSpec
    {
        public long InstanceId { get; }
        public long LeaderboardId { get; }
        public LeaderboardState State { get; }
        public long ActivationDate { get; }
        public bool Visible { get; }

        public LeaderboardInstanceSpec(long instanceId, long leaderboardId, LeaderboardState state, long activationDate, bool visible)
        {
            InstanceId = instanceId;
            LeaderboardId = leaderboardId;
            State = state;
            ActivationDate = activationDate;
            Visible = visible;
        }

        public static LeaderboardInstanceSpec From(DBLeaderboardInstance instance)
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            return new(instance.InstanceId, instance.LeaderboardId, instance.State, instance.ActivationDate, instance.Visible);
        }
    }

    public sealed record LeaderboardEntryWrite
    {
        private readonly byte[] _ruleStates;

        public long InstanceId { get; }
        public long ParticipantId { get; }
        public long Score { get; }
        public long HighScore { get; }
        public byte[] RuleStates => (byte[])_ruleStates.Clone();

        public LeaderboardEntryWrite(long instanceId, long participantId, long score, long highScore, byte[] ruleStates)
        {
            InstanceId = instanceId;
            ParticipantId = participantId;
            Score = score;
            HighScore = highScore;
            _ruleStates = ruleStates?.ToArray() ?? throw new ArgumentNullException(nameof(ruleStates));
        }

        public static LeaderboardEntryWrite From(DBLeaderboardEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            return new(entry.InstanceId, entry.ParticipantId, entry.Score, entry.HighScore, entry.RuleStates);
        }
    }

    public sealed record LeaderboardMetaMapping
    {
        public long LeaderboardId { get; }
        public long InstanceId { get; }
        public long SubLeaderboardId { get; }
        public long SubInstanceId { get; }

        public LeaderboardMetaMapping(long leaderboardId, long instanceId, long subLeaderboardId, long subInstanceId)
        {
            LeaderboardId = leaderboardId;
            InstanceId = instanceId;
            SubLeaderboardId = subLeaderboardId;
            SubInstanceId = subInstanceId;
        }

        public static LeaderboardMetaMapping From(DBMetaEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            return new(entry.LeaderboardId, entry.InstanceId, entry.SubLeaderboardId, entry.SubInstanceId);
        }
    }

    public sealed record LeaderboardRewardWrite
    {
        public long LeaderboardId { get; }
        public long InstanceId { get; }
        public long RewardId { get; }
        public long ParticipantId { get; }
        public int Rank { get; }
        public long CreationDate { get; }

        public LeaderboardRewardWrite(long leaderboardId, long instanceId, long rewardId, long participantId, int rank, long creationDate)
        {
            LeaderboardId = leaderboardId;
            InstanceId = instanceId;
            RewardId = rewardId;
            ParticipantId = participantId;
            Rank = rank;
            CreationDate = creationDate;
        }

        public static LeaderboardRewardWrite From(DBRewardEntry entry)
        {
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            return new(entry.LeaderboardId, entry.InstanceId, entry.RewardId, entry.ParticipantId, entry.Rank, entry.CreationDate);
        }
    }

    internal static class LeaderboardStoreRecords
    {
        public static IReadOnlyList<T> Copy<T>(IEnumerable<T> values, string parameterName)
        {
            if (values == null)
                throw new ArgumentNullException(parameterName);

            T[] copy = values.ToArray();
            if (copy.Any(value => value is null))
                throw new ArgumentException("Collection cannot contain null values.", parameterName);

            return Array.AsReadOnly(copy);
        }

        public static IReadOnlyList<TOutput> Convert<TInput, TOutput>(IEnumerable<TInput> values, Func<TInput, TOutput> converter, string parameterName)
        {
            if (converter == null)
                throw new ArgumentNullException(nameof(converter));

            return Copy(values, parameterName).Select(converter).ToArray();
        }

        public static void RequireNonNegative(int value, string parameterName)
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(parameterName, "Value must not be negative.");
        }

        public static void RequireSameInstance(IEnumerable<LeaderboardEntryWrite> entries, long instanceId, string parameterName)
        {
            if (entries.Any(entry => entry.InstanceId != instanceId))
                throw new ArgumentException("Entries must belong to the requested instance.", parameterName);
        }

        public static void RequireSameRewardScope(IEnumerable<LeaderboardRewardWrite> rewards, long leaderboardId, long instanceId, string parameterName)
        {
            if (rewards.Any(reward => reward.LeaderboardId != leaderboardId || reward.InstanceId != instanceId))
                throw new ArgumentException("Rewards must belong to the requested leaderboard instance.", parameterName);

            if (rewards.GroupBy(reward => reward.ParticipantId).Any(group => group.Skip(1).Any()))
                throw new ArgumentException("Rewards cannot contain duplicate participants.", parameterName);
        }
    }
}
