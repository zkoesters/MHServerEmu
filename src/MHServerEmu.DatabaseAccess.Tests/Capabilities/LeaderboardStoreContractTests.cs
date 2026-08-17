using System.Reflection;
using MHServerEmu.DatabaseAccess.Persistence;
using MHServerEmu.DatabaseAccess.Models.Leaderboards;

namespace MHServerEmu.DatabaseAccess.Tests.Capabilities
{
    public class LeaderboardStoreContractTests
    {
        [Fact]
        public void LeaderboardStoreResult_HasExpectedOutcomes()
        {
            Assert.Equal(new[]
            {
                LeaderboardStoreResult.Success,
                LeaderboardStoreResult.NotFound,
                LeaderboardStoreResult.Conflict,
                LeaderboardStoreResult.StaleState,
                LeaderboardStoreResult.InvalidData,
                LeaderboardStoreResult.Failed,
                LeaderboardStoreResult.OutcomeUncertain,
            }, Enum.GetValues<LeaderboardStoreResult>());
        }

        [Fact]
        public void RewardFinalizationResult_HasExpectedOutcomes()
        {
            Assert.Equal(new[]
            {
                RewardFinalizationResult.Finalized,
                RewardFinalizationResult.AlreadyFinalized,
                RewardFinalizationResult.NotFound,
                RewardFinalizationResult.Failed,
                RewardFinalizationResult.OutcomeUncertain,
            }, Enum.GetValues<RewardFinalizationResult>());
        }

        [Fact]
        public void ILeaderboardStore_ExposesExactOperationSet()
        {
            Type storeType = typeof(ILeaderboardStore);
            MethodInfo[] methods = storeType.GetMethods();

            Assert.Equal(13, methods.Length);
            Assert.Contains(methods, method => method.Name == nameof(ILeaderboardStore.Initialize)
                && method.ReturnType == typeof(LeaderboardStoreResult));
            Assert.Contains(methods, method => method.Name == nameof(ILeaderboardStore.LoadVisibleInstances)
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[]
                {
                    typeof(long), typeof(long), typeof(int), typeof(IReadOnlyList<DBLeaderboardInstance>).MakeByRefType(),
                }));
            Assert.Contains(methods, method => method.Name == nameof(ILeaderboardStore.FinalizeReward)
                && method.ReturnType == typeof(RewardFinalizationResult)
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[]
                {
                    typeof(LeaderboardRewardKey), typeof(long),
                }));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void PersistenceFatalFailure_RejectsBlankCode(string code)
        {
            Assert.Throws<ArgumentException>(() => new PersistenceFatalFailure(code, "initialize"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        public void PersistenceFatalFailure_RejectsBlankOperation(string operation)
        {
            Assert.Throws<ArgumentException>(() => new PersistenceFatalFailure("persistence_failure", operation));
        }
    }
}
