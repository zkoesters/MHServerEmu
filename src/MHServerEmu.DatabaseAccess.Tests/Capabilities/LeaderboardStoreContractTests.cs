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
            AssertMethod(methods, nameof(ILeaderboardStore.Initialize), typeof(LeaderboardStoreResult));
            AssertMethod(methods, nameof(ILeaderboardStore.ReconcileSchedule), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardReconciliation), typeof(LeaderboardSnapshot).MakeByRefType() }, 1);
            AssertMethod(methods, nameof(ILeaderboardStore.LoadEntries), typeof(LeaderboardStoreResult), new[] { typeof(long), typeof(IReadOnlyList<DBLeaderboardEntry>).MakeByRefType() }, 1);
            AssertMethod(methods, nameof(ILeaderboardStore.LoadInstance), typeof(LeaderboardStoreResult), new[] { typeof(long), typeof(long), typeof(DBLeaderboardInstance).MakeByRefType() }, 2);
            AssertMethod(methods, nameof(ILeaderboardStore.LoadVisibleInstances), typeof(LeaderboardStoreResult), new[] { typeof(long), typeof(long), typeof(int), typeof(IReadOnlyList<DBLeaderboardInstance>).MakeByRefType() }, 3);
            AssertMethod(methods, nameof(ILeaderboardStore.ActivateInstance), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardActivation) });
            AssertMethod(methods, nameof(ILeaderboardStore.SaveScoreBatch), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardScoreBatch) });
            AssertMethod(methods, nameof(ILeaderboardStore.ExpireInstance), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardExpiration) });
            AssertMethod(methods, nameof(ILeaderboardStore.RotateActiveInstance), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardRotation), typeof(DBLeaderboardInstance).MakeByRefType() }, 1);
            AssertMethod(methods, nameof(ILeaderboardStore.MaintainVisibility), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardVisibilityRequest), typeof(LeaderboardVisibilitySnapshot).MakeByRefType() }, 1);
            AssertMethod(methods, nameof(ILeaderboardStore.GenerateRewards), typeof(LeaderboardStoreResult), new[] { typeof(LeaderboardRewardGeneration) });
            AssertMethod(methods, nameof(ILeaderboardStore.GetPendingRewards), typeof(LeaderboardStoreResult), new[] { typeof(long), typeof(IReadOnlyList<DBRewardEntry>).MakeByRefType() }, 1);
            AssertMethod(methods, nameof(ILeaderboardStore.FinalizeReward), typeof(RewardFinalizationResult), new[] { typeof(LeaderboardRewardKey), typeof(long) });
        }

        [Fact]
        public void ILeaderboardStore_DeclaresDetachedOutputContract()
        {
            LeaderboardStoreOutputContractAttribute contract = typeof(ILeaderboardStore).GetCustomAttribute<LeaderboardStoreOutputContractAttribute>();

            Assert.NotNull(contract);
            Assert.True(contract.ReturnsDetachedRows);
            Assert.True(contract.CopiesRuleStates);
            Assert.True(contract.EmptyOutputsOnFailure);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(101)]
        public void ValidateVisibleInstancesLimit_RejectsValuesOutsidePageBounds(int limit)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => LeaderboardStoreValidator.ValidateVisibleInstancesLimit(limit));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(100)]
        public void ValidateVisibleInstancesLimit_AcceptsPageBoundaries(int limit)
        {
            LeaderboardStoreValidator.ValidateVisibleInstancesLimit(limit);
        }

        [Fact]
        public void LoadVisibleInstances_DeclaresPaginationContract()
        {
            MethodInfo method = typeof(ILeaderboardStore).GetMethod(nameof(ILeaderboardStore.LoadVisibleInstances));
            LeaderboardPaginationContractAttribute contract = method.GetCustomAttribute<LeaderboardPaginationContractAttribute>();

            Assert.NotNull(contract);
            Assert.Equal(1, contract.MinimumLimit);
            Assert.Equal(100, contract.MaximumLimit);
            Assert.True(contract.RequiresValidationBeforeConnection);
            Assert.Equal(LeaderboardStoreResult.InvalidData, contract.InvalidLimitResult);
            Assert.True(contract.EmptyOutputOnInvalidLimit);
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

        private static void AssertMethod(IEnumerable<MethodInfo> methods, string name, Type returnType, Type[] parameterTypes = null, params int[] outParameterIndices)
        {
            parameterTypes ??= Array.Empty<Type>();
            MethodInfo method = Assert.Single(methods.Where(method => method.Name == name
                && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(parameterTypes)));

            int[] actualOutParameterIndices = method.GetParameters()
                .Select((parameter, index) => (parameter, index))
                .Where(value => value.parameter.IsOut)
                .Select(value => value.index)
                .ToArray();
            Assert.Equal(outParameterIndices, actualOutParameterIndices);
        }
    }
}
